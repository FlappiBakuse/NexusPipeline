'use strict';
const crypto = require('node:crypto');
const path = require('node:path');
const { strictJson } = require('./strict-json.cjs');
const { setLocale, text } = require('./native-text.cjs');

function createTrayLifecycle(config, bridge, windows, electron, startup) {
  let mode = config.mode;
  let busy = false;
  let tray;
  let ended = false;
  let refresh;
  let showing;
  let exitState;
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
  let ready = false;
  function menu() { if (ready && !ended) tray?.setToolTip(exitState?.stage !== 'idle' && exitState ? text('closing') : 'NexusPipeline'); }
  async function run(action, closing = false) {
    if (busy || ended) return;
    busy = true; menu();
    try { await action(); }
    catch (error) {
      if (!ended) {
        if (closing || ['desktop_writes_pending', 'desktop_draft_preparation_timeout'].includes(error.message)) await show();
        else await electron.dialog.showMessageBox({ type: 'warning', title: 'NexusPipeline', message: text('failure') });
      }
    }
    finally { busy = false; menu(); }
  }
  async function refreshLocale() {
    const saved = await json('control/v1/settings/interface');
    config.locale = saved.values.locale; setLocale(config.locale); menu();
  }
  async function show() {
    if (!ready) {
      config.visible = true;
      if (mode === 'desktop') await windows.show();
      return;
    }
    if (mode === 'browser') await electron.shell.openExternal(new URL(config.route, config.address).href);
    else await windows.show();
  }
  function activate() {
    if (ended) return Promise.resolve();
    if (!showing) showing = show().catch(() => {
      if (!ended) tray?.setToolTip(text('failure'));
    }).finally(() => { showing = undefined; });
    return showing;
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
    tray = new electron.Tray(path.join(__dirname, 'nexuspipeline.ico'));
    tray.setToolTip('NexusPipeline'); menu();
    tray.on('click', activate);
    tray.on('double-click', activate);
    startup.subscribe(value => {
      if (value.kind === 'stage') tray.setToolTip(`NexusPipeline · ${value.stage + 1}/${value.total}`);
      if (value.kind === 'failed') tray.setToolTip('NexusPipeline · ' + (config.locale === 'zh-CN' ? '启动失败' : 'Startup failed'));
    });
    tray.on('right-click', () => {
      if (refresh || ended) return;
      refresh = startup.menu(mode === 'browser', busy || (!ready && !startup.failed), config.locale)
        .finally(() => { refresh = undefined; });
      return refresh.then(async action => {
        if (ended) return;
        if (action === 3 && startup.failed) { startup.dismiss(); return; }
        if (!ready) return;
        if (action === 1) await run(changeMode);
        if (action === 2) await run(reload);
        if (action === 3) await run(() => bridge.requestClose(), true);
      }).catch(() => { if (!ended) tray?.setToolTip(text('failure')); });
    });
    if (config.visible && mode === 'desktop') await windows.show();
  }
  async function complete() {
    ready = true; tray.setToolTip('NexusPipeline');
    await refreshLocale();
    if (config.visible && mode === 'browser') await show();
  }
  return Object.freeze({ start, complete, show: activate, refreshMenu: menu,
    exitState(value) { exitState = value; menu(); }, dispose() { ended = true; tray?.destroy(); } });
}
module.exports = { createTrayLifecycle };
