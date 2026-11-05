'use strict';
const { ipcMain, dialog, shell } = require('electron');
const crypto = require('node:crypto');
const { readProof, readSession, readClosePermit, readClosePending, readMode, responseText } = require('./proof-reader.cjs');
const { installFrontendBridge } = require('./native-frontend.cjs');
const { installLoginBridge } = require('./native-login.cjs');

function installNativeBridge(window, config, page, authorizedQuit, setLocale = () => {}) {
  const host = new URL(config.address);
  let session;
  let generation = 0;
  let preparing;
  let busy = false;
  let timer;
  let ended = false;
  let connecting;
  let frontend;
  let login;
  function validateSender(event) {
    if (!window || window.isDestroyed() || event.sender !== window.webContents || event.senderFrame !== window.webContents.mainFrame ||
        event.senderFrame.url.split('#')[0] !== page) throw new Error('desktop_source_denied');
  }
  function invalidate() {
    frontend?.invalidate();
    login?.invalidate();
    generation++; session = undefined;
    if (preparing) { preparing.reject(new Error('desktop_document_changed')); preparing = undefined; }
  }
  const publishWindowState = () => {
    if (window && !window.isDestroyed()) window.webContents.send('desktop.window-state', { maximized: window.isMaximized() });
  };
  function attachWindow(value) {
    window = value;
    if (!value) return;
    value.webContents.on('did-start-navigation', (_event, _url, inPlace, mainFrame) => { if (mainFrame && !inPlace) invalidate(); });
    value.webContents.on('render-process-gone', invalidate);
    value.on('maximize', publishWindowState);
    value.on('unmaximize', publishWindowState);
    value.on('closed', () => { if (window === value) { invalidate(); window = undefined; } });
  }
  attachWindow(window);
  async function connect() {
    const current = generation;
    const challenge = crypto.randomUUID();
    const response = await fetch(new URL('control/v1/desktop/session?correlationId=' + challenge, host), { signal: AbortSignal.timeout(5000) });
    const value = readSession(await responseText(response), config.verificationKey, config.instanceId, challenge);
    if (current !== generation) throw new Error('desktop_document_changed');
    session = value;
    return value;
  }
  async function connectWhenReady() {
    if (session) return session;
    if (!connecting) connecting = (async () => {
      const until = performance.now() + 30000;
      while (!ended) {
        try { return await connect(); }
        catch { if (performance.now() >= until) throw new Error('desktop_host_startup_unavailable'); await new Promise(resolve => setTimeout(resolve, 100)); }
      }
      throw new Error('desktop_window_ended');
    })().finally(() => { connecting = undefined; });
    return await connecting;
  }
  async function prepareRenderer(reason = 'close') {
    if (!window) return;
    if (preparing || window.isDestroyed() || window.webContents.isCrashed()) throw new Error('desktop_draft_preparation_unavailable');
    const token = crypto.randomUUID();
    const current = generation;
    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => { preparing = undefined; reject(new Error('desktop_draft_preparation_timeout')); }, 10000);
      preparing = { token, resolve: accepted => {
        clearTimeout(timeout); preparing = undefined;
        accepted && current === generation ? resolve() : reject(new Error('desktop_drafts_not_prepared'));
      }, reject: error => { clearTimeout(timeout); reject(error); } };
      window.webContents.send('desktop.prepare-exit', token, reason);
    });
  }
  function requestIdentity(correlationId) {
    if (!session) throw new Error('desktop_session_required');
    return { format: 'nexus.desktop.channel', version: 1, instanceId: session.instanceId,
      clientId: session.clientId, sessionId: session.sessionId, correlationId };
  }
  async function consumePermit(text, request, preparedGeneration) {
    const current = preparedGeneration ?? generation;
    const permit = readClosePermit(text, config.verificationKey, request);
    if (preparedGeneration === undefined) await prepareRenderer('maintenance');
    if (current !== generation || Date.parse(permit.expiresAt) <= Date.now()) throw new Error('desktop_close_expired');
    await authorizedQuit();
  }
  async function requestClose() {
    if (busy) throw new Error('desktop_action_busy');
    busy = true;
    let closingWindow; let wasEnabled = false;
    try {
      const current = generation;
      await prepareRenderer();
      closingWindow = window; wasEnabled = closingWindow?.isEnabled() === true;
      if (wasEnabled) closingWindow.setEnabled(false);
      await connect();
      if (current !== generation) throw new Error('desktop_document_changed');
      const until = performance.now() + 30000;
      while (!ended && current === generation && performance.now() < until) {
        const request = { ...requestIdentity(crypto.randomUUID()), type: 'close.request' };
        const response = await fetch(new URL('control/v1/desktop/authorize', host), {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request),
          signal: AbortSignal.timeout(Math.max(1, Math.min(5000, Math.ceil(until - performance.now())))) });
        if (response.status === 409) {
          if (!await readClosePending(response)) throw new Error('desktop_response_unconfirmed');
          await new Promise(resolve => setTimeout(resolve, 100));
          continue;
        }
        await consumePermit(await responseText(response), request, current);
        return;
      }
      throw new Error('desktop_close_pending');
    } finally {
      if (wasEnabled && window === closingWindow && !closingWindow.isDestroyed()) closingWindow.setEnabled(true);
      busy = false;
    }
  }
  async function pollMaintenance() {
    if (ended) return;
    let acquired = false;
    try {
      if (!busy) {
        if (!session) await connectWhenReady();
        const current = generation;
        const request = requestIdentity(crypto.randomUUID());
        const query = new URLSearchParams({ sessionId: request.sessionId, correlationId: request.correlationId });
        const response = await fetch(new URL('control/v1/desktop/maintenance?' + query, host), { signal: AbortSignal.timeout(5000) });
        if (response.status === 204) await response.body?.cancel();
        else {
          const text = await responseText(response);
          const permit = readClosePermit(text, config.verificationKey, request);
          while (busy && !ended && Date.parse(permit.expiresAt) > Date.now())
            await new Promise(resolve => setTimeout(resolve, 50));
          if (ended || current !== generation) throw new Error('desktop_document_changed');
          busy = true; acquired = true;
          await consumePermit(text, request);
        }
      }
    } catch { }
    finally { if (acquired) busy = false; if (!ended) timer = setTimeout(pollMaintenance, 2000); }
  }
  frontend = installFrontendBridge(config, validateSender, connectWhenReady, () => generation);
  login = installLoginBridge(config, () => window, connectWhenReady, () => generation);
  ipcMain.handle('desktop.connect', async event => {
    validateSender(event);
    return { ...await connectWhenReady(), route: config.route };
  });
  ipcMain.handle('desktop.prepared', (event, token, accepted) => {
    validateSender(event);
    if (preparing?.token !== token || typeof accepted !== 'boolean') throw new Error('desktop_preparation_mismatch');
    preparing.resolve(accepted);
  });
  ipcMain.handle('desktop.locale', (event, locale) => {
    validateSender(event);
    if (locale !== 'zh-CN' && locale !== 'en-US') throw new Error('desktop_locale_invalid');
    setLocale(locale);
  });
  ipcMain.handle('desktop.action', async (event, input) => {
    validateSender(event);
    if (!session || !input || Object.keys(input).some(key => !['type', 'url', 'sessionId'].includes(key)) ||
        input.sessionId !== session.sessionId || input.url !== undefined && input.type !== 'external.open')
      throw new Error('desktop_request_rejected');
    if (input.type === 'window.state') return { maximized: window.isMaximized() };
    if (input.type === 'window.minimize') { window.minimize(); return; }
    if (input.type === 'window.maximize') {
      if (window.isMaximized()) window.unmaximize(); else window.maximize();
      return;
    }
    if (input.type === 'window.close') { window.close(); return; }
    if (input.type === 'close.request') return await requestClose();
    if (input.type === 'hide') { window.hide(); return; }
    if (input.type === 'show') { window.show(); return; }
    if (busy || !['file.select', 'folder.select', 'external.open'].includes(input.type)) throw new Error('desktop_action_unavailable');
    busy = true;
    const current = generation;
    try {
      const request = { ...requestIdentity(crypto.randomUUID()), type: input.type, ...(input.url === undefined ? {} : { url: input.url }) };
      const response = await fetch(new URL('control/v1/desktop/authorize', host), { method: 'POST',
        headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request), signal: AbortSignal.timeout(5000) });
      const reply = readProof(await responseText(response), config.verificationKey, 'nexus.desktop.channel');
      validateSender(event);
      if (current !== generation || ['instanceId', 'clientId', 'sessionId', 'correlationId'].some(key => reply[key] !== request[key]))
        throw new Error('desktop_reply_mismatch');
      if (input.type === 'external.open') {
        const url = new URL(input.url);
        if (url.protocol !== 'https:' || url.username || url.password) throw new Error('desktop_url_denied');
        await shell.openExternal(url.href);
        return { opened: true };
      }
      const result = await dialog.showOpenDialog(window, { properties: [input.type === 'file.select' ? 'openFile' : 'openDirectory'] });
      return { canceled: result.canceled, paths: result.canceled ? [] : result.filePaths };
    } finally { busy = false; }
  });
  async function dispose() {
    frontend.dispose();
    ended = true; clearTimeout(timer); invalidate();
    for (const name of ['desktop.connect', 'desktop.prepared', 'desktop.action', 'desktop.locale']) ipcMain.removeHandler(name);
    await login.dispose();
  }
  async function changeWindow(change) {
    if (busy) throw new Error('desktop_action_busy');
    busy = true;
    try { await prepareRenderer(); return await change(); }
    finally { busy = false; }
  }
  async function publishMode(mode) {
    if (!['desktop', 'browser'].includes(mode)) throw new Error('desktop_mode_invalid');
    await connectWhenReady();
    const request = { ...requestIdentity(crypto.randomUUID()), format: 'nexus.desktop.mode', mode };
    const response = await fetch(new URL('control/v1/desktop/mode', host), { method: 'POST',
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request), signal: AbortSignal.timeout(5000) });
    readMode(await responseText(response), config.verificationKey, request);
  }
  return Object.freeze({ requestClose, changeWindow, publishMode, attachWindow, dispose, start: async () => {
    await connectWhenReady();
    await login.start();
    if (!ended) timer = setTimeout(pollMaintenance, 2000);
  } });
}
module.exports = { installNativeBridge };
