'use strict';
const crypto = require('node:crypto');
const { strictJson } = require('./strict-json.cjs');
const { setLocale, text } = require('./native-text.cjs');

function createTrayLifecycle(config, bridge, windows, electron) {
  let mode = config.mode;
  let busy = false;
  let tray;
  let ended = false;
  let refresh;
  let contextMenu;
  async function json(path, init) {
    const response = await fetch(new URL(path, config.address), {
      ...init, redirect: 'error', signal: AbortSignal.timeout(5000)
    });
    if (!response.ok) { await response.body?.cancel(); throw new Error('desktop_settings_rejected'); }
    const chunks = []; let length = 0;
    for await (const chunk of response.body) {
      length += chunk.length;
      if (length > 131072) throw new Error('desktop_settings_limit');
      chunks.push(chunk);
    }
    return strictJson(new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks)));
  }
  function menu() {
    if (!tray || ended) return;
    contextMenu = electron.Menu.buildFromTemplate([
      { label: text('show'), enabled: !busy, click: () => run(show) },
      { label: text('hide'), enabled: !busy && mode === 'desktop', click: () => windows.hide() },
      { label: text('lightweight'), type: 'checkbox', checked: mode === 'browser', enabled: !busy,
        click: () => run(changeMode) },
      { label: text('reload'), enabled: !busy, click: () => run(reload) },
      { type: 'separator' },
      { label: text('quit'), enabled: !busy, click: () => run(() => bridge.requestClose()) }
    ]);
  }
  async function run(action) {
    if (busy || ended) return;
    busy = true; menu();
    try { await action(); }
    catch (error) {
      if (!ended && error.message !== 'desktop_drafts_not_prepared')
        await electron.dialog.showMessageBox({ type: 'warning', title: 'NexusPipeline', message: text('failure') });
    }
    finally { busy = false; menu(); }
  }
  async function refreshLocale() {
    const saved = await json('control/v1/settings/interface');
    setLocale(saved.values.locale); menu();
  }
  async function show() {
    if (mode === 'browser') await electron.shell.openExternal(new URL(config.route, config.address).href);
    else await windows.show();
  }
  async function reload() {
    if (mode === 'browser') return await show();
    await bridge.changeWindow(() => windows.reload());
  }
  async function changeMode() {
    const next = mode === 'desktop' ? 'browser' : 'desktop';
    await bridge.changeWindow(async () => {
      const source = await json('control/v1/settings');
      const values = { ...source.saved.values, desktop: { ...source.saved.values.desktop, mode: next } };
      await json('control/v1/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ format: 'nexus.control.host-settings', version: 1,
          operationId: crypto.randomUUID(), expectedRevision: source.saved.revision, values }) });
      mode = next;
      if (next === 'browser') windows.release();
      else await windows.show();
      await bridge.publishMode(next);
    });
  }
  async function start() {
    tray = new electron.Tray(await electron.app.getFileIcon(process.execPath, { size: 'small' }));
    tray.setToolTip('NexusPipeline'); menu();
    tray.on('click', () => run(show));
    tray.on('right-click', () => {
      if (refresh || ended) return;
      refresh = (async () => {
        await refreshLocale().catch(() => {});
        if (!ended) tray.popUpContextMenu(contextMenu);
      })().finally(() => { refresh = undefined; });
    });
    await refreshLocale();
    if (config.visible) await show();
  }
  return Object.freeze({ start, refreshMenu: menu, dispose() { ended = true; tray?.destroy(); } });
}
module.exports = { createTrayLifecycle };
