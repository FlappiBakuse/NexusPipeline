'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { loadHostPage } = require('../../apps/desktop/host-page.cjs');

test('desktop waits for its fixed Host page after transient listener startup failures', async () => {
  const urls = [];
  const window = { isDestroyed: () => false, async loadURL(url) {
    urls.push(url);
    if (urls.length < 3) throw Object.assign(new Error('connection refused'), { code: 'ERR_CONNECTION_REFUSED' });
  } };
  await loadHostPage(window, 'http://127.0.0.1:1234/');
  assert.deepEqual(urls, Array(3).fill('http://127.0.0.1:1234/'));
});

test('desktop does not retry permanent page failures or a destroyed window', async () => {
  for (const [code, destroyedAfterFailure] of [['ERR_BLOCKED_BY_CLIENT', false], ['ERR_CONNECTION_REFUSED', true]]) {
    let attempts = 0;
    const failure = Object.assign(new Error('page rejected'), { code });
    const window = { isDestroyed: () => attempts > 0 && destroyedAfterFailure, async loadURL() { attempts++; throw failure; } };
    await assert.rejects(loadHostPage(window, 'http://127.0.0.1:1234/'), error => error.code === code && error.message === 'page rejected');
    assert.equal(attempts, 1);
  }
});

test('desktop bounds a page load that never settles and clears its deadline timer', async () => {
  let cleared = false;
  const sandbox = { module: { exports: {} }, performance: { now: () => 0 },
    setTimeout(callback, delay) { assert.equal(delay, 30000); queueMicrotask(callback); return 1; },
    clearTimeout(id) { assert.equal(id, 1); cleared = true; } };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../apps/desktop/host-page.cjs'), 'utf8'), sandbox);
  const window = { isDestroyed: () => false, loadURL: () => new Promise(() => {}) };
  await assert.rejects(sandbox.module.exports.loadHostPage(window, 'http://127.0.0.1:1234/'), /desktop_host_startup_unavailable/);
  assert.equal(cleared, true);
});
