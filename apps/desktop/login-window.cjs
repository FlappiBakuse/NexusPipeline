'use strict';
const { BrowserWindow, session, Menu } = require('electron');
const { text } = require('./native-text.cjs');
const { isReady, collectValues, watchResponses, origin } = require('./login-values.cjs');
const denyPermission = (_contents, _permission, answer) => answer(false);
const denyCapability = () => false;
const denyDownload = event => event.preventDefault();

function createLoginWindow(offer, parent, onCleaned, validateCandidate) {
  const policy = offer.policy;
  const privateSession = session.fromPartition('nexus-login-' + offer.captureId + '-' + offer.nonce, { cache: false });
  const windows = new Set(); const observers = new Set(); const pending = new Set();
  const passive = Object.create(null);
  let root; let timer; let navigating = false; let sampling = false; let rejected = false; let closing = false; let cleanup; let settled = false; let bootstrapping = true;
  privateSession.setPermissionRequestHandler(denyPermission);
  privateSession.setPermissionCheckHandler(denyCapability);
  privateSession.setDevicePermissionHandler(denyCapability);
  privateSession.on('will-download', denyDownload);
  const preferences = { session: privateSession, contextIsolation: true, sandbox: true, nodeIntegration: false,
    nodeIntegrationInSubFrames: false, webSecurity: true, webviewTag: false, devTools: false, spellcheck: false,
    disableDialogs: true, navigateOnDragDrop: false };
  const diagnostic = (stage, details = {}) => console.error(JSON.stringify({ kind: 'browser-login-stage', stage, ...details }));
  function allowed(url) { return policy.origins.includes(origin(url)); }
  privateSession.webRequest.onBeforeRequest({ urls: ['<all_urls>'] }, (details, answer) => {
    const bootstrap = details.url === 'about:blank' && bootstrapping && !closing;
    const denied = ['mainFrame', 'subFrame'].includes(details.resourceType) && !bootstrap && !allowed(details.url);
    if (denied) diagnostic('frame-rejected', { category: details.resourceType });
    answer({ cancel: denied });
  });
  function configure(window, primary) {
    windows.add(window); window.setMenu(null);
    const contents = window.webContents;
    contents.on('did-fail-load', (_event, code, _description, _url, mainFrame) => diagnostic('load-failed', { code, mainFrame }));
    contents.on('did-finish-load', () => diagnostic('loaded'));
    contents.on('dom-ready', () => diagnostic('dom-ready'));
    contents.on('will-attach-webview', event => event.preventDefault());
    contents.on('will-frame-navigate', event => { if (!allowed(event.url)) event.preventDefault(); });
    contents.on('will-redirect', (event, url) => { if (!allowed(url)) event.preventDefault(); });
    contents.on('render-process-gone', () => { if (!closing) void finish('failed'); });
    contents.on('unresponsive', () => { if (!closing) void finish('failed'); });
    contents.setWindowOpenHandler(details => {
      if (closing || windows.size >= 4 || !policy.popupOrigins.includes(origin(details.url))) return { action: 'deny' };
      return { action: 'allow', overrideBrowserWindowOptions: { parent: root, autoHideMenuBar: true, webPreferences: preferences } };
    });
    contents.on('did-create-window', popup => {
      if (closing || windows.size >= 4 || !allowed(popup.webContents.getURL()) && popup.webContents.getURL() !== '') { popup.destroy(); return; }
      configure(popup, false);
    });
    window.on('close', event => { if (primary && !closing) { event.preventDefault(); void finish('canceled'); } });
    window.on('closed', () => windows.delete(window));
    const observer = watchResponses(contents, policy, passive, pending, () => closing, () => { if (!closing) void finish('failed'); });
    observers.add(observer); return observer.ready;
  }
  async function clearSession() {
    for (const observer of observers) { try { observer.dispose(); } catch { } }
    observers.clear();
    await Promise.all([...windows].map(window => new Promise((resolve, reject) => {
      if (window.isDestroyed()) { windows.delete(window); resolve(); return; }
      const timeout = setTimeout(() => reject(new Error('browser_login_window_not_closed')), 5000);
      window.once('closed', () => { clearTimeout(timeout); resolve(); }); window.destroy();
    })));
    if (windows.size !== 0) throw new Error('browser_login_window_not_closed');
    await Promise.allSettled([...pending]);
    await privateSession.closeAllConnections();
    await privateSession.clearStorageData();
    await privateSession.clearCache();
    await privateSession.clearAuthCache();
    await privateSession.clearHostResolverCache();
    if ((await privateSession.cookies.get({})).length !== 0) throw new Error('browser_login_storage_not_cleared');
    privateSession.webRequest.onBeforeRequest(null);
    privateSession.removeListener('will-download', denyDownload);
    for (const key of Object.keys(passive)) delete passive[key];
  }
  async function finish(status, values = Object.create(null)) {
    if (settled) return;
    if (cleanup) return cleanup;
    diagnostic(status === 'canceled' ? 'canceled' : 'cleanup', { status });
    closing = true; clearTimeout(timer);
    cleanup = (async () => {
      try {
        await clearSession();
        await onCleaned(status, values); settled = true;
      } catch {
        diagnostic('cleanup-failed'); cleanup = undefined;
      } finally { for (const key of Object.keys(values)) delete values[key]; }
    })();
    return cleanup;
  }
  async function sample(explicit = false) {
    if (closing || navigating || sampling || rejected && !explicit) return;
    sampling = true;
    let values;
    try {
      values = await collectValues(policy, privateSession, windows, passive);
      if (!closing && isReady(policy, values)) {
        diagnostic('validating');
        const accepted = await validateCandidate(values);
        diagnostic(accepted ? 'validation-accepted' : 'validation-rejected');
        if (closing) return;
        if (accepted) { await finish('completed', values); return; }
        rejected = true;
        root?.setTitle(text('login') + ' · ' + text('loginRejected'));
      } else if (explicit) root?.setTitle(text('login') + ' · ' + text('loginIncomplete'));
    } catch {
      if (!closing) { rejected = true; diagnostic('validation-unavailable'); root?.setTitle(text('login') + ' · ' + text('loginRetry')); }
    } finally {
      sampling = false;
      if (values) for (const key of Object.keys(values)) delete values[key];
    }
    if (!closing && !rejected) timer = setTimeout(() => sample(), 250);
  }
  async function navigate() {
    if (closing || navigating || sampling) return;
    navigating = true; rejected = false; clearTimeout(timer);
    root.setTitle(text('login') + ' · ' + text('loginLoading'));
    diagnostic('navigating');
    try {
      await root.loadURL(policy.startUrl);
      if (!closing) timer = setTimeout(sample, 50);
    } catch (error) {
      if (!closing) {
        rejected = true;
        diagnostic('navigation-unavailable', { code: Number.isInteger(error?.errno) ? error.errno : 0 });
        root.setTitle(text('login') + ' · ' + text('loginLoadFailed'));
      }
    } finally { navigating = false; }
  }
  const started = (async () => {
    root = new BrowserWindow({ width: 1000, height: 760, minWidth: 640, minHeight: 480, title: text('login'),
      show: true, autoHideMenuBar: true, ...(parent && !parent.isDestroyed() ? { parent } : {}), webPreferences: preferences });
    diagnostic('window-created');
    root.setTitle(text('login') + ' · ' + text('loginLoading'));
    diagnostic('capture-initializing');
    const ready = configure(root, true);
    root.setAutoHideMenuBar(false);
    root.setMenu(Menu.buildFromTemplate([{ label: text('loginActions'), submenu: [
      { label: text('loginRefresh'), click: () => { diagnostic('refresh'); void navigate(); } },
      { label: text('loginComplete'), click: () => { clearTimeout(timer); void sample(true); } },
      { label: text('cancel'), click: () => void finish('canceled') }
    ] }]));
    await root.loadURL('about:blank'); bootstrapping = false;
    await ready; diagnostic('cdp-attached');
    if (!closing) await navigate();
  })();
  void started.catch(() => finish('failed'));
  return Object.freeze({ cancel: () => finish('canceled'), retry: () => closing ? finish('failed') : Promise.resolve(), started });
}
module.exports = { createLoginWindow };
