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
      send(channel, token, reason) {
        if (channel !== 'desktop.prepare-exit') return;
        preparations.push(reason);
        queueMicrotask(() => handlers.get('desktop.prepared')(
          { sender: window.webContents, senderFrame: window.webContents.mainFrame }, token, scenario.prepared ? 'ready' : 'pending-writes'));
      }
    }
  };
  const sandbox = {
    module: { exports: {} }, URL, URLSearchParams, AbortController, AbortSignal, Date, performance: { now: () => clock },
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
        assert.equal(enabled, true);
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
    else await assert.rejects(bridge.requestClose(), /desktop_writes_pending/);
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
  api.prepareExit(async reason => { reasons.push(reason); return 'ready'; });
  for (const reason of ['close', 'maintenance', 'unknown', undefined])
    await listeners.get('desktop.prepare-exit')({}, 'nonce', reason);
  assert.deepEqual(reasons, ['close', 'maintenance']);
  assert.deepEqual(replies.map(reply => reply[2]), ['ready', 'ready', 'pending-writes', 'pending-writes']);
});

function exitFixture() {
  const handlers = new Map(), timers = new Map(), events = new Map(), messages = [];
  let time = 0, nextTimer = 0, quit = 0, authorizations = 0;
  const window = { isDestroyed: () => false, isEnabled: () => true, setEnabled() {}, on() {},
    webContents: { mainFrame: { url: 'http://127.0.0.1:1234/' }, isCrashed: () => false,
      on(name, callback) { events.set(name, callback); },
      send(channel, token, reason) { messages.push({ channel, token, reason }); } } };
  const context = {
    module: { exports: {} }, URL, URLSearchParams, AbortController, AbortSignal, Date,
    performance: { now: () => time },
    setTimeout(callback, delay) { timers.set(++nextTimer, { callback, due: time + delay }); return nextTimer; },
    clearTimeout(id) { timers.delete(id); },
    require(name) {
      if (name === 'electron') return { ipcMain: {
        handle: (key, value) => handlers.set(key, value), removeHandler: key => handlers.delete(key) } };
      if (name === './native-frontend.cjs') return { installFrontendBridge: () => ({ invalidate() {}, dispose() {} }) };
      if (name === './native-login.cjs') return { installLoginBridge: () => ({ start: async () => {}, invalidate() {}, dispose: async () => {} }) };
      if (name === './proof-reader.cjs') return {
        readSession: () => ({ instanceId: 'fixture', clientId: 'client', sessionId: 'session' }),
        responseText: async response => response.text(),
        readClosePermit: () => ({ expiresAt: new Date(Date.now() + 30000).toISOString() })
      };
      return require(name);
    },
    fetch: async url => {
      if (url.pathname.endsWith('/authorize')) authorizations++;
      return { text: async () => '{}' };
    }
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../apps/desktop/native-bridge.cjs'), 'utf8'), context);
  const bridge = context.module.exports.installNativeBridge(window,
    { address: 'http://127.0.0.1:1234/', instanceId: 'fixture' }, 'http://127.0.0.1:1234/', () => { quit++; });
  const event = { sender: window.webContents, senderFrame: window.webContents.mainFrame };
  return { bridge, messages, handlers, events,
    get quit() { return quit; }, get authorizations() { return authorizations; },
    get token() { return messages.filter(message => message.channel === 'desktop.prepare-exit').at(-1).token; },
    reply(token, accepted) { return handlers.get('desktop.prepared')(event, token, accepted); },
    advance(milliseconds) {
      time += milliseconds;
      for (const [id, timer] of [...timers]) if (timer.due <= time) { timers.delete(id); timer.callback(); }
    }
  };
}

test('duplicate close requests share one bounded preparation and one fresh Host permit', async () => {
  const fixture = exitFixture();
  try {
    const first = fixture.bridge.requestClose(), second = fixture.bridge.requestClose();
    assert.equal(fixture.messages.filter(message => message.channel === 'desktop.prepare-exit').length, 1);
    assert.equal(fixture.reply('foreign-token', 'ready'), false);
    assert.equal(fixture.reply(fixture.token, 'ready'), true);
    await Promise.all([first, second]);
    assert.equal(fixture.authorizations, 1); assert.equal(fixture.quit, 1);
    assert.deepEqual(fixture.messages.filter(item => item.channel === 'desktop.exit-state').map(item => item.token.stage),
      ['preparing', 'authorizing', 'idle']);
  } finally { await fixture.bridge.dispose(); }
});

test('pending writes release close preparation and a later retry can exit', async () => {
  const fixture = exitFixture();
  try {
    const pending = assert.rejects(fixture.bridge.requestClose(), /desktop_writes_pending/);
    fixture.reply(fixture.token, 'pending-writes'); await pending;
    assert.equal(fixture.authorizations, 0); assert.equal(fixture.quit, 0);
    assert.equal(fixture.messages.at(-1).token.stage, 'idle');
    assert.equal(fixture.messages.at(-1).token.code, 'desktop_writes_pending');
    const retry = fixture.bridge.requestClose(); fixture.reply(fixture.token, 'ready'); await retry;
    assert.equal(fixture.authorizations, 1); assert.equal(fixture.quit, 1);
  } finally { await fixture.bridge.dispose(); }
});

test('an unresponsive renderer is cancelled and its late reply cannot replace the next preparation', async () => {
  const fixture = exitFixture();
  try {
    const timed = assert.rejects(fixture.bridge.requestClose(), /desktop_draft_preparation_timeout/);
    const old = fixture.token;
    fixture.advance(10000); await timed;
    assert(fixture.messages.some(message => message.channel === 'desktop.cancel-exit' && message.token === old));
    assert.equal(fixture.reply(old, 'ready'), false);
    const next = fixture.bridge.requestClose();
    assert.equal(fixture.reply(old, 'ready'), false); assert.equal(fixture.authorizations, 0);
    fixture.reply(fixture.token, 'ready'); await next;
    assert.equal(fixture.authorizations, 1); assert.equal(fixture.quit, 1);
  } finally { await fixture.bridge.dispose(); }
});

test('replacing the document cancels preparation and rejects its old reply', async () => {
  const fixture = exitFixture();
  try {
    const result = assert.rejects(fixture.bridge.requestClose(), /desktop_document_changed/);
    const token = fixture.token;
    fixture.events.get('did-start-navigation')({}, 'http://127.0.0.1:1234/', false, true);
    await result;
    assert(fixture.messages.some(message => message.channel === 'desktop.cancel-exit' && message.token === token));
    assert.equal(fixture.reply(token, 'ready'), false);
    assert.equal(fixture.authorizations, 0); assert.equal(fixture.quit, 0);
  } finally { await fixture.bridge.dispose(); }
});

test('preload suppresses the retired callback reply without keeping a user-wait stage', async () => {
  const listeners = new Map(), calls = [], stages = [];
  let api, release, started;
  const pending = new Promise(resolve => { release = resolve; });
  const waiting = new Promise(resolve => { started = resolve; });
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../apps/desktop/preload.cjs'), 'utf8'), {
    process: { isMainFrame: true }, console: { info: value => stages.push(JSON.parse(value).stage) },
    require: () => ({ contextBridge: { exposeInMainWorld: (_name, value) => { api = value; } },
      ipcRenderer: { on: (name, callback) => listeners.set(name, callback),
        invoke: async (...args) => { calls.push(args); return true; } } })
  });
  api.prepareExit(async () => { started(); return pending; });
  const work = listeners.get('desktop.prepare-exit')({}, 'nonce', 'close'); await waiting;
  listeners.get('desktop.cancel-exit')({}, 'nonce'); release('ready'); await work;
  assert.equal(calls.filter(call => call[0] === 'desktop.prepared').length, 0);
  assert(stages.includes('cancelled') && !stages.includes('waiting-user'));
});

test('a rejected late IPC reply settles in preload without an unhandled rejection', async () => {
  const listeners = new Map(), stages = [];
  let api;
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../apps/desktop/preload.cjs'), 'utf8'), {
    process: { isMainFrame: true }, console: { info: value => stages.push(JSON.parse(value).stage) },
    require: () => ({ contextBridge: { exposeInMainWorld: (_name, value) => { api = value; } },
      ipcRenderer: { on: (name, callback) => listeners.set(name, callback),
        invoke: async () => { throw new Error('retired'); } } })
  });
  api.prepareExit(async () => 'ready');
  await listeners.get('desktop.prepare-exit')({}, 'nonce', 'close');
  assert(stages.includes('reply-unavailable'));
});
