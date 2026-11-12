'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

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
