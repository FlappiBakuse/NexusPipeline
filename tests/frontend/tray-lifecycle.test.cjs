'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const nativeText = require('../../apps/desktop/native-text.cjs');

function fixture(mode = 'desktop') {
  const state = { menus: [], shown: 0, hidden: 0, released: 0, reloaded: 0, quit: 0, warnings: [],
    posts: [], locale: 'zh-CN', revision: 4, allow: true, rejectSave: false, destroyed: false, events: new Map() };
  const source = { autoStart: true, desktop: { mode, minimizeToTray: true, openOnStartup: false },
    service: { webPort: 1234 }, extensions: [{ extensionId: 'unchanged', enabled: true }] };
  const sandbox = { module: { exports: {} }, URL, AbortSignal, Buffer, TextDecoder, process,
    require(name) { return name.startsWith('./') ? require('../../apps/desktop/' + name.slice(2)) : require(name); },
    async fetch(url, init) {
      if (url.pathname.endsWith('/interface')) return new Response(JSON.stringify({ values: { locale: state.locale } }));
      if (init.method !== 'POST') return new Response(JSON.stringify({ saved: { revision: state.revision, values: source } }));
      const request = JSON.parse(init.body); state.posts.push(request);
      if (state.rejectSave) return new Response('{}', { status: 409 });
      assert.equal(request.expectedRevision, state.revision);
      assert.deepEqual({ ...request.values, desktop: source.desktop }, source);
      source.desktop = request.values.desktop; state.revision++;
      return new Response(JSON.stringify({ saved: { revision: state.revision, values: source } }));
    } };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../apps/desktop/tray-lifecycle.cjs'), 'utf8'), sandbox);
  const bridge = { requestClose: async () => { state.quit++; }, publishMode: async mode => { state.effective = mode; }, changeWindow: async callback => {
    if (!state.allow) throw new Error('desktop_drafts_not_prepared');
    return await callback();
  } };
  const windows = { show: async () => { state.shown++; }, hide: () => { state.hidden++; },
    release: () => { state.released++; }, reload: async () => { state.reloaded++; } };
  const electron = { app: { getFileIcon: async () => ({}) },
    Menu: { buildFromTemplate: value => { state.menus.push(value); state.menu = value; return value; } },
    Tray: class { setToolTip() {} setContextMenu(value) { state.menu = value; }
      popUpContextMenu(value) { state.popup = value; }
      on(name, callback) { state.events.set(name, callback); } destroy() { state.destroyed = true; } },
    shell: { openExternal: async value => { state.opened = value; } },
    dialog: { showMessageBox: async value => { state.warnings.push(value); } } };
  const lifecycle = sandbox.module.exports.createTrayLifecycle(
    { mode, address: 'http://127.0.0.1:1234/', route: '/settings', visible: false }, bridge, windows, electron);
  return { state, lifecycle, click: index => state.menu[index].click() };
}

test('tray releases the desktop only after prepared drafts and a successful shared settings CAS', async () => {
  const f = fixture(); await f.lifecycle.start();
  await f.click(0); assert.equal(f.state.shown, 1);
  await f.click(1); assert.equal(f.state.hidden, 1);
  await f.click(3); assert.equal(f.state.reloaded, 1);
  f.state.allow = false; await f.click(2);
  assert.equal(f.state.posts.length, 0); assert.equal(f.state.released, 0); assert.equal(f.state.warnings.length, 0);
  f.state.allow = true; f.state.rejectSave = true; await f.click(2);
  assert.equal(f.state.released, 0); assert.equal(f.state.menu[2].checked, false); assert.equal(f.state.warnings.length, 1);
  f.state.rejectSave = false; await f.click(2);
  assert.equal(f.state.released, 1); assert.equal(f.state.menu[2].checked, true);
  assert.equal(f.state.effective, 'browser');
  await f.click(0); assert.equal(f.state.opened, 'http://127.0.0.1:1234/settings');
  await f.click(3); assert.equal(f.state.reloaded, 1);
  await f.click(2); assert.equal(f.state.shown, 2); assert.equal(f.state.menu[2].checked, false);
  assert.equal(f.state.posts.at(-1).values.desktop.mode, 'desktop');
  await f.click(5); assert.equal(f.state.quit, 1);
  f.lifecycle.dispose(); assert.equal(f.state.destroyed, true);
});

test('browser tray starts without a renderer and updates existing native labels from saved interface locale', async () => {
  const f = fixture('browser'); await f.lifecycle.start();
  assert.equal(f.state.shown, 0); assert.equal(f.state.menu[2].checked, true);
  assert.equal(f.state.menu[0].label, '显示');
  f.state.locale = 'en-US'; f.state.events.get('right-click')();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.state.menu[0].label, 'Show'); assert.equal(f.state.menu[2].label, 'Lightweight mode');
  assert.equal(f.state.popup[0].label, 'Show');
  assert.equal(nativeText.text('saveQuestion'), 'Allow saving to the selected file?');
  assert.equal(nativeText.text('login'), 'Sign in');
  await f.click(2); assert.equal(f.state.shown, 1);
  f.lifecycle.dispose();
});
