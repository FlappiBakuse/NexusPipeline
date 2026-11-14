'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

function fixture(mode = 'desktop') {
  const state = { menus: [], shown: 0, released: 0, reloaded: 0, quit: 0, warnings: [],
    posts: [], locale: 'zh-CN', revision: 4, allow: true, rejectSave: false, destroyed: false, events: new Map() };
  const source = { autoStart: true, desktop: { mode, minimizeToTray: true, openOnStartup: false },
    service: { webPort: 1234 }, extensions: [{ extensionId: 'unchanged', enabled: true }] };
  const sandbox = { module: { exports: {} }, __dirname: path.join(__dirname, '../../apps/desktop'), URL, AbortSignal, Buffer, TextDecoder, process,
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
  const bridge = { requestClose: async () => { state.quit++; if (state.waitQuit) await state.waitQuit;
    if (state.failQuit) throw new Error('desktop_close_pending'); }, publishMode: async mode => { state.effective = mode; }, changeWindow: async callback => {
    if (!state.allow) throw new Error('desktop_writes_pending');
    return await callback();
  } };
  const windows = { show: async () => { state.shown++; },
    release: () => { state.released++; }, reload: async () => { state.reloaded++; } };
  const electron = {
    Tray: class { setToolTip(value) { state.tooltip = value; }
      on(name, callback) { state.events.set(name, callback); } destroy() { state.destroyed = true; } },
    shell: { openExternal: async value => { state.opened = value; } },
    dialog: { showMessageBox: async value => { state.warnings.push(value); } } };
  let action = 0;
  const startup = { subscribe(callback) { state.progress = callback; }, failed: false,
    menu: async (lightweight, disabled, locale) => {
      state.menus.push({ lightweight, disabled, locale }); return action;
    }, dismiss() { state.dismissed = true; } };
  const lifecycle = sandbox.module.exports.createTrayLifecycle(
    { mode, address: 'http://127.0.0.1:1234/', route: '/settings', visible: false }, bridge, windows, electron, startup);
  return { state, lifecycle, startup, async click(value) {
    action = value; await state.events.get('right-click')();
  } };
}

test('tray releases the desktop only after write readiness and a successful shared settings CAS', async () => {
  const f = fixture(); await f.lifecycle.start(); await f.lifecycle.complete();
  try {
    await f.lifecycle.show(); assert.equal(f.state.shown, 1);
    await f.click(2); assert.equal(f.state.reloaded, 1);
    f.state.allow = false; await f.click(1);
    assert.equal(f.state.posts.length, 0); assert.equal(f.state.released, 0); assert.equal(f.state.warnings.length, 0);
    f.state.allow = true; f.state.rejectSave = true; await f.click(1);
    assert.equal(f.state.released, 0); assert.equal(f.state.warnings.length, 1);
    await f.click(0); assert.equal(f.state.menus.at(-1).lightweight, false);
    f.state.rejectSave = false; await f.click(1);
    assert.equal(f.state.released, 1); assert.equal(f.state.effective, 'browser');
    await f.click(0); assert.equal(f.state.menus.at(-1).lightweight, true);
    await f.lifecycle.show(); assert.equal(f.state.opened, 'http://127.0.0.1:1234/settings');
    await f.click(2); assert.equal(f.state.reloaded, 1);
    await f.click(1); assert.equal(f.state.shown, 3);
    assert.equal(f.state.posts.at(-1).values.desktop.mode, 'desktop');
    assert.equal(f.state.effective, 'desktop');
    await f.click(3); assert.equal(f.state.quit, 1);
  } finally { f.lifecycle.dispose(); }
  assert.equal(f.state.destroyed, true);
});

test('browser tray prepares the native menu with saved locale and no renderer', async () => {
  const f = fixture('browser'); f.state.locale = 'en-US';
  await f.lifecycle.start();
  try {
    await f.click(0);
    assert.equal(f.state.menus.at(-1).lightweight, true);
    assert.equal(f.state.menus.at(-1).disabled, true);
    assert.equal(f.state.shown, 0);
    await f.lifecycle.complete(); await f.click(0);
    assert.deepEqual(f.state.menus.at(-1), { lightweight: true, disabled: false, locale: 'en-US' });
    await f.click(1); assert.equal(f.state.shown, 1);
    assert.equal(f.state.effective, 'desktop');
  } finally { f.lifecycle.dispose(); }
});

test('failed startup dismisses its own surface without requesting Host close', async () => {
  const f = fixture(); await f.lifecycle.start();
  try {
    f.startup.failed = true;
    await f.click(3);
    assert.equal(f.state.dismissed, true);
    assert.equal(f.state.quit, 0);
  } finally { f.lifecycle.dispose(); }
});


test('pending quit leaves click, double click and native menu available and releases after failure', async () => {
  const f = fixture(); await f.lifecycle.start(); await f.lifecycle.complete();
  let release;
  f.state.waitQuit = new Promise(resolve => { release = resolve; });
  try {
    const quitting = f.click(3);
    for (let index = 0; index < 8; index++) await Promise.resolve();
    assert.equal(f.state.quit, 1);
    await f.state.events.get('click')(); await f.state.events.get('double-click')();
    assert.equal(f.state.shown, 2);
    const before = f.state.menus.length;
    await f.click(0);
    assert.equal(f.state.menus.length, before + 1);
    assert.equal(f.state.menus.at(-1).disabled, true);
    release(); await quitting;
    f.state.waitQuit = undefined; f.state.failQuit = true;
    const shown = f.state.shown;
    await f.click(3); await f.click(0);
    assert.equal(f.state.menus.at(-1).disabled, false);
    assert(f.state.shown > shown);
    assert.equal(f.state.warnings.length, 0);
    f.state.failQuit = false; await f.click(3);
    assert.equal(f.state.quit, 3);
  } finally { release(); f.lifecycle.dispose(); }
});
