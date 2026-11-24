'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

test('file and folder dialogs remember only successful choices in their live editor', async () => {
  const handlers = new Map(), events = new Map(), options = [], approved = [];
  const page = 'http://127.0.0.1:10001/';
  const docs = 'C:\\Users\\test\\Documents';
  const identity = { sessionId: 'test' };
  const window = { isDestroyed: () => false, on: (name, callback) => events.set(name, callback),
    webContents: { mainFrame: { url: page }, on: (name, callback) => events.set(name, callback) } };
  const event = { sender: window.webContents, senderFrame: window.webContents.mainFrame };
  let api, pending, next;
  const electron = {
    app: { getPath(name) { assert.equal(name, 'documents'); return docs; } },
    dialog: { async showOpenDialog(parent, value) {
      assert.equal(parent, window); options.push(value);
      return await next;
    } },
    ipcMain: { handle: (name, callback) => handlers.set(name, callback), removeHandler: name => handlers.delete(name) },
    ipcRenderer: { on() {}, invoke: (name, ...args) => handlers.get(name)(event, ...args) },
    contextBridge: { exposeInMainWorld: (_name, value) => { api = value; } }
  };
  const sandbox = { module: { exports: {} }, URL, URLSearchParams, AbortSignal, AbortController, performance,
    process: { isMainFrame: true }, setTimeout: () => 1, clearTimeout() {},
    require(name) {
      if (name === 'electron') return electron;
      if (name === './proof-reader.cjs') return { readSession: () => identity, responseText: async value => value, readProof: value => value };
      if (name === './diagnostics.cjs') return require('../../apps/desktop/diagnostics.cjs');
      if (name === './native-login.cjs') return { installLoginBridge: () => ({ invalidate() {}, dispose() {} }) };
      return require(name);
    },
    async fetch(url, init) {
      if (!url.pathname.endsWith('/authorize')) return identity;
      const request = JSON.parse(init.body);
      assert.equal(request.pickerId, undefined);
      approved.push(request.type);
      return request;
    } };
  const source = name => fs.readFileSync(path.join(__dirname, '../../apps/desktop', name), 'utf8');
  vm.runInNewContext(source('native-bridge.cjs'), sandbox);
  const bridge = sandbox.module.exports.installNativeBridge(window, { address: page }, page, () => {});
  vm.runInNewContext(source('preload.cjs'), sandbox);
  await api.connect();
  const first = '00000000-0000-4000-8000-000000000001', other = '00000000-0000-4000-8000-000000000002';
  async function choose(method, scope, selected) {
    next = { canceled: !selected, filePaths: selected ? [selected] : [] };
    await api[method](scope);
    return options.at(-1).defaultPath;
  }
  assert.equal(await choose('chooseFile', first, 'D:\\Scripts\\run.exe'), docs);
  assert.equal(await choose('chooseFolder', first, 'D:\\Game'), 'D:\\Scripts');
  assert.equal(await choose('chooseFile', first), 'D:\\Game');
  assert.equal(await choose('chooseFile', other, 'E:\\Hooks\\post.cmd'), docs);
  assert.equal(await choose('chooseFile', first), 'D:\\Game');
  assert.equal(await choose('chooseFolder', other), 'E:\\Hooks');
  next = new Promise(resolve => { pending = resolve; });
  const late = api.chooseFile(first);
  while (options.length < 7) await new Promise(resolve => setImmediate(resolve));
  await api.releasePathPicker(first);
  pending({ canceled: false, filePaths: ['X:\\obsolete.exe'] });
  assert.equal((await late).canceled, true);
  assert.equal(await choose('chooseFile', first), docs);
  events.get('did-start-navigation')({}, page, false, true);
  await api.connect();
  assert.equal(await choose('chooseFile', other), docs);
  assert.deepEqual(approved, ['file.select', 'folder.select', 'file.select', 'file.select', 'file.select',
    'folder.select', 'file.select', 'file.select', 'file.select']);
  await bridge.dispose();
});
