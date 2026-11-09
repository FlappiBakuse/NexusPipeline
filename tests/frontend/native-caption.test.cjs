'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

test('drawn window actions require the bound current main-frame session and retain native close handling', async () => {
  const handlers = new Map(), events = new Map(), publications = [], actions = [], requests = [];
  const identity = { instanceId: 'instance', clientId: 'client', sessionId: 'session' };
  const page = 'http://127.0.0.1:1234/';
  let maximized = false, quit = false;
  const window = {
    isDestroyed: () => false, isMaximized: () => maximized,
    minimize: () => actions.push('minimize'), close: () => actions.push('native-close'),
    maximize() { maximized = true; events.get('maximize')(); },
    unmaximize() { maximized = false; events.get('unmaximize')(); },
    on: (name, callback) => events.set(name, callback),
    webContents: { mainFrame: { url: page },
      on: (name, callback) => events.set(name, callback),
      send: (name, value) => publications.push({ name, maximized: value.maximized }) },
  };
  const sandbox = {
    module: { exports: {} }, URL, URLSearchParams, AbortSignal, Date, performance,
    setTimeout: () => 1, clearTimeout() {},
    require(name) {
      if (name === './native-frontend.cjs') return { installFrontendBridge: () => ({ invalidate() {}, dispose() {} }) };
      if (name === './native-login.cjs') return { installLoginBridge: () => ({ start: async () => {}, invalidate() {}, dispose: async () => {} }) };
      if (name === 'electron') return { ipcMain: { handle: (name, callback) => handlers.set(name, callback),
        removeHandler: name => handlers.delete(name) } };
      if (name === './proof-reader.cjs') return { readSession: () => identity, responseText: async () => '{}' };
      return require(name);
    },
    async fetch(url) { requests.push(url.pathname); return {}; },
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../apps/desktop/native-bridge.cjs'), 'utf8'), sandbox);
  const bridge = sandbox.module.exports.installNativeBridge(window, { address: page }, page, () => { quit = true; });
  const event = { sender: window.webContents, senderFrame: window.webContents.mainFrame };
  const action = (type, extra = {}) => handlers.get('desktop.action')(event, { type, sessionId: identity.sessionId, ...extra });
  await assert.rejects(action('window.minimize', { sessionId: undefined }), /desktop_request_rejected/);
  await bridge.start();
  await assert.rejects(action('window.minimize', { sessionId: 'foreign' }), /desktop_request_rejected/);
  await assert.rejects(action('window.close', { url: 'https://example.invalid/' }), /desktop_request_rejected/);
  await assert.rejects(handlers.get('desktop.action')({ ...event, senderFrame: { url: page } },
    { type: 'window.close', sessionId: identity.sessionId }), /desktop_source_denied/);
  assert.equal((await action('window.state')).maximized, false);
  await action('window.minimize');
  await action('window.maximize'); assert.equal((await action('window.state')).maximized, true);
  assert.deepEqual(publications.at(-1), { name: 'desktop.window-state', maximized: true });
  await action('window.maximize'); assert.equal((await action('window.state')).maximized, false);
  assert.deepEqual(publications.at(-1), { name: 'desktop.window-state', maximized: false });
  await action('window.close');
  assert.deepEqual(actions, ['minimize', 'native-close']);
  assert.equal(quit, false);
  assert(requests.every(path => path === '/control/v1/desktop/session'));
  events.get('did-start-navigation')({}, page, false, true);
  await assert.rejects(action('window.minimize'), /desktop_request_rejected/);
  events.get('closed')();
  await assert.rejects(action('window.minimize'), /desktop_source_denied/);
  const replacement = { ...window, webContents: { ...window.webContents, mainFrame: { url: page } } };
  bridge.attachWindow(replacement);
  const current = { sender: replacement.webContents, senderFrame: replacement.webContents.mainFrame };
  await handlers.get('desktop.connect')(current);
  await handlers.get('desktop.action')(current, { type: 'window.minimize', sessionId: identity.sessionId });
  await assert.rejects(action('window.minimize'), /desktop_source_denied/);
  await bridge.dispose(); assert.equal(handlers.size, 0);
});
