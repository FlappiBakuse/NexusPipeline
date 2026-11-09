'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

for (const scenario of [
  { name: 'a pending read-only maintenance poll allows the user to close the window', prepared: true },
  { name: 'an accepted pending close waits and requests a fresh correlated permit', prepared: true, pending: true },
  { name: 'a foreign close conflict is never retried', prepared: true, conflict: true },
  { name: 'a pending close has a bounded deadline', prepared: true, pending: true, expires: true },
  { name: 'a changed document cannot continue a pending close', prepared: true, pending: true, navigation: true },
  { name: 'an unprepared renderer prevents user close before requesting authorization', prepared: false },
  { name: 'an unprepared renderer prevents maintenance exit despite a valid permit', prepared: false, maintenance: true },
  { name: 'a prepared renderer accepts the signed maintenance exit reason', prepared: true, maintenance: true },
  { name: 'an invalid maintenance permit never requests renderer preparation', prepared: true, maintenance: true, invalidPermit: true }
]) test(scenario.name, async () => {
  const handlers = new Map();
  const timers = new Map();
  let timerId = 0;
  let finishPoll;
  let quit = false;
  let authorized = false;
  let enabled = true;
  let clock = 0, navigating;
  const authorizationIds = [];
  const preparations = [];
  const identity = { instanceId: 'instance', clientId: 'client', sessionId: 'session' };
  const window = {
    isDestroyed: () => false,
    isEnabled: () => enabled,
    setEnabled(value) { enabled = value; },
    on() {},
    webContents: {
      mainFrame: { url: 'http://127.0.0.1:1234/' },
      on(name, callback) { if (name === 'did-start-navigation') navigating = callback; },
      isCrashed: () => false,
      send(_channel, token, reason) {
        preparations.push(reason);
        queueMicrotask(() => handlers.get('desktop.prepared')(
          { sender: window.webContents, senderFrame: window.webContents.mainFrame }, token, scenario.prepared));
      }
    }
  };
  const sandbox = {
    module: { exports: {} }, URL, URLSearchParams, AbortSignal, Date, performance: { now: () => clock },
    setTimeout: (callback, delay) => {
      if (delay === 100) { clock += delay; queueMicrotask(() => { if (scenario.navigation) navigating({}, page, false, true); callback(); }); }
      else timers.set(++timerId, { callback, delay });
      return timerId;
    },
    clearTimeout: id => timers.delete(id),
    require(name) {
      if (name === './native-frontend.cjs') return { installFrontendBridge: () => ({ invalidate() {}, dispose() {} }) };
      if (name === './native-login.cjs') return { installLoginBridge: () => ({ start: async () => {}, invalidate() {}, dispose: async () => {} }) };
      if (name === 'electron') return {
        ipcMain: { handle: (key, callback) => handlers.set(key, callback), removeHandler: key => handlers.delete(key) }
      };
      if (name === './proof-reader.cjs') return {
        readSession: () => identity,
        readClosePending: async response => response.pending === true,
        responseText: async response => response.text(),
        readClosePermit: () => {
          if (scenario.invalidPermit) throw new Error('desktop_proof_invalid');
          return { expiresAt: new Date(Date.now() + 30000).toISOString() };
        }
      };
      return require(name);
    },
    async fetch(url, options) {
      if (url.pathname.endsWith('/maintenance')) return await new Promise(resolve => { finishPoll = resolve; });
      if (url.pathname.endsWith('/authorize')) {
        assert.equal(enabled, false);
        authorized = true; authorizationIds.push(JSON.parse(options.body).correlationId);
        if (scenario.conflict) return { status: 409, pending: false };
        if (scenario.pending && (authorizationIds.length === 1 || scenario.expires)) return { status: 409, pending: true };
      }
      return { text: async () => '{}' };
    }
  };
  const page = 'http://127.0.0.1:1234/';
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../apps/desktop/native-bridge.cjs'), 'utf8'), sandbox);
  const bridge = sandbox.module.exports.installNativeBridge(window,
    { address: 'http://127.0.0.1:1234/', instanceId: 'instance' }, 'http://127.0.0.1:1234/', () => { quit = true; });
  await bridge.start();
  const poll = [...timers.values()][0].callback();
  assert.equal(typeof finishPoll, 'function');
  if (scenario.maintenance) {
    finishPoll({ status: 200, text: async () => '{}' });
  } else {
    if (scenario.conflict) await assert.rejects(bridge.requestClose(), /desktop_response_unconfirmed/);
    else if (scenario.expires || scenario.navigation) await assert.rejects(bridge.requestClose(), /desktop_close_pending/);
    else if (scenario.prepared) await bridge.requestClose();
    else await assert.rejects(bridge.requestClose(), /desktop_drafts_not_prepared/);
    finishPoll({ status: 204, body: { cancel: async () => {} } });
  }
  await poll;
  assert.equal(authorized, !scenario.maintenance && scenario.prepared);
  assert.equal(quit, scenario.prepared && !scenario.invalidPermit && !scenario.conflict && !scenario.expires && !scenario.navigation);
  assert.equal(new Set(authorizationIds).size, authorizationIds.length);
  if (!quit) assert.equal(enabled, true);
  if (scenario.conflict || scenario.navigation) assert.equal(authorizationIds.length, 1);
  if (scenario.pending && !scenario.navigation) {
    if (scenario.expires) { assert(clock >= 30000 && clock < 31000); assert(authorizationIds.length > 1); }
    else assert.equal(authorizationIds.length, 2);
  }
  assert.deepEqual(preparations, scenario.invalidPermit ? [] : [scenario.maintenance ? 'maintenance' : 'close']);
  await bridge.dispose();
  assert.equal(handlers.size, 0);
});

test('preload forwards only known native exit reasons to the renderer', async () => {
  const listeners = new Map();
  const replies = [];
  let api;
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../apps/desktop/preload.cjs'), 'utf8'), {
    process: { isMainFrame: true },
    require: () => ({
      contextBridge: { exposeInMainWorld: (_name, value) => { api = value; } },
      ipcRenderer: { on: (name, callback) => listeners.set(name, callback),
        invoke: async (...args) => { replies.push(args); } }
    })
  });
  const reasons = [];
  api.prepareExit(async reason => { reasons.push(reason); return true; });
  for (const reason of ['close', 'maintenance', 'unknown', undefined])
    await listeners.get('desktop.prepare-exit')({}, 'nonce', reason);
  assert.deepEqual(reasons, ['close', 'maintenance']);
  assert.deepEqual(replies.map(reply => reply[2]), [true, true, false, false]);
});
