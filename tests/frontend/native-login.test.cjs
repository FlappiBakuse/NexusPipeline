'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { EventEmitter } = require('node:events');

test('automatic login entry requires the exact start-page query in both processes', async () => {
  const { createLoginPageActions } = require('../../apps/desktop/login-page-actions.cjs');
  const contents = new EventEmitter();
  const start = 'https://example.test/login?source=desktop';
  const other = 'https://example.test/login?source=other';
  let url = other, code;
  contents.mainFrame = { get url() { return url; } };
  contents.getURL = () => url;
  contents.isLoadingMainFrame = contents.isDestroyed = () => false;
  contents.executeJavaScriptInIsolatedWorld = async (_world, scripts) => { code = scripts[0].code; return false; };
  const stop = createLoginPageActions(contents, { startUrl: start,
    pageActions: { clicks: [{ target: { selector: 'button', kind: 'visible' } }], stopWhen: [] } }, () => true);
  contents.emit('did-stop-loading');
  assert.equal(code, undefined);
  url = start + '#section';
  contents.emit('did-stop-loading');
  assert.equal(typeof code, 'string');
  assert.equal(vm.runInNewContext(code, { location: new URL(other), performance }), false);
  await stop();
});

test('an empty native login wait does not block startup or issue another request before it ends', async () => {
  const module = { exports: {} }, timers = new Map();
  let requests = 0, id = 0;
  const context = {
    module, URL, AbortController, AbortSignal,
    setTimeout: callback => { timers.set(++id, callback); return id; },
    clearTimeout: token => timers.delete(token),
    fetch: async (_url, options) => {
      requests++;
      return new Promise((resolve, reject) => {
        options.signal.addEventListener('abort', () => reject(options.signal.reason), { once: true });
      });
    },
    require(name) {
      if (name === './proof-reader.cjs') return { readLogin: () => ({ offers: [] }), responseText: async () => '' };
      if (name === './diagnostics.cjs') return require('../../apps/desktop/diagnostics.cjs');
      if (name === './login-window.cjs') return { createLoginWindow: () => { throw Error('unexpected login window'); } };
      return require(name);
    }
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../apps/desktop/native-login.cjs'), 'utf8'), context);
  const bridge = module.exports.installLoginBridge({ address: 'http://localhost/' }, null,
    async () => ({ instanceId: 'instance', clientId: 'client', sessionId: 'session' }), () => 1);
  await bridge.start();
  assert.equal(requests, 1);
  assert.equal(timers.size, 0);
  await bridge.dispose();
  assert.equal(requests, 1);
  assert.equal(timers.size, 0);
});

test('login failure diagnostics preserve a safe reason and never serialize response or startup data', () => {
  const { describeFailure, recordFailure } = require('../../apps/desktop/diagnostics.cjs');
  const messages = [];
  const sink = console.error;
  console.error = value => messages.push(JSON.parse(value));
  try {
    recordFailure('desktop-login', 'complete', new TypeError('nonce=private capture body', {
      cause: Object.assign(new Error('content=private'), { code: 'ECONNRESET', token: 'private' })
    }), '12345678-0000-0000-0000-000000000000');
    recordFailure('desktop-login', 'poll', new Error('desktop_proof_invalid'), 'capture-secret');
  } finally { console.error = sink; }
  assert.deepEqual(messages, [
    { kind: 'desktop-login', stage: 'complete', type: 'TypeError', reason: 'ECONNRESET', correlationId: '12345678-0000-0000-0000-000000000000' },
    { kind: 'desktop-login', stage: 'poll', type: 'Error', reason: 'desktop_proof_invalid' }
  ]);
  assert.deepEqual(describeFailure({ name: 'private', code: 'secret', message: 'desktop_x_private' }), { type: 'Error', reason: 'unclassified' });
});

test('native login continues independent cleanup and waits for a proved clean session before completion', async () => {
  const calls = [], records = [];
  const makeStep = stage => async () => { calls.push(stage); };
  let failing = true;
  const session = new EventEmitter();
  Object.assign(session, {
    setPermissionRequestHandler() {}, setPermissionCheckHandler() {}, setDevicePermissionHandler() {},
    webRequest: { onBeforeRequest(value) { if (value === null) calls.push('release-policy'); } },
    closeAllConnections: makeStep('connections'), clearCache: makeStep('cache'),
    clearAuthCache: makeStep('auth-cache'), clearHostResolverCache: makeStep('resolver-cache'),
    clearStorageData: async () => { calls.push('storage'); if (failing) throw new Error('private storage detail'); },
    cookies: { get: async () => { calls.push('cookies'); return failing ? [{}] : []; } }
  });
  class BrowserWindow extends EventEmitter {
    webContents = new EventEmitter();
    contentView = { addChildView() {} };
    setMenu() {} show() {}
    getContentSize() { return [1080, 800]; }
    isDestroyed() { return false; }
    destroy() { calls.push('window'); this.emit('closed'); }
  }
  class WebContentsView {
    webContents = Object.assign(new EventEmitter(), {
      setWindowOpenHandler() {}, isDestroyed() { return false; },
      close() { calls.push('view'); this.emit('destroyed'); },
      async loadURL() {}
    });
    setBounds() {}
  }
  const module = { exports: {} };
  const modulePath = path.join(__dirname, '../../apps/desktop/login-window.cjs');
  vm.runInNewContext(fs.readFileSync(modulePath, 'utf8'), {
    module, setTimeout, clearTimeout, setImmediate, clearImmediate,
    require(name) {
      if (name === 'electron') return { BrowserWindow, WebContentsView, session: { fromPartition: () => session } };
      if (name === './native-text.cjs') return { text: value => value };
      if (name === './login-shell.cjs') return { configureLoginShell: () => ({ load: async () => {} }) };
      if (name === './login-page-actions.cjs') return { createLoginPageActions: () => () => {} };
      if (name === './login-values.cjs') return {
        watchResponses: () => ({ ready: Promise.resolve(), dispose: () => calls.push('observer') })
      };
      if (name === './diagnostics.cjs') return { describeFailure: require('../../apps/desktop/diagnostics.cjs').describeFailure,
        recordDiagnostic: (kind, stage, details) => records.push({ kind, stage, details }) };
      throw new Error(`Unexpected module ${name}`);
    }
  });
  const value = module.exports.createLoginWindow({ captureId: 'private capture', nonce: 'private nonce',
    policy: { startUrl: 'https://example.invalid/' } }, null, async status => { calls.push(status); });
  await value.started;
  await value.cancel();
  assert.deepEqual(calls, ['observer', 'view', 'window', 'connections', 'storage', 'cache', 'auth-cache', 'resolver-cache', 'cookies']);
  const failure = records.find(item => item.stage === 'cleanup-failed');
  assert.equal(failure.details.primary.stage, 'storage');
  assert.equal(failure.details.cleanup[0].reason, 'browser_login_storage_not_cleared');
  assert.doesNotMatch(JSON.stringify(records), /private/);
  await value.retry();
  assert.equal(records.filter(item => item.stage === 'cleanup-failed').length, 1);
  assert.equal(records.filter(item => ['cleanup', 'canceled'].includes(item.stage)).length, 1);
  failing = false;
  await value.retry();
  assert.deepEqual(calls.slice(-2), ['release-policy', 'canceled']);
});
