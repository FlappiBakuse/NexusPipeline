'use strict';
const { BrowserWindow, session, Menu } = require('electron');
const { text } = require('./native-text.cjs');
const { isReady, collectValues, watchResponses, origin } = require('./login-values.cjs');
const { describeFailure, recordDiagnostic } = require('./diagnostics.cjs');
const denyPermission = (_contents, _permission, answer) => answer(false);
const denyCapability = () => false;
const denyDownload = event => event.preventDefault();

function createLoginWindow(offer, parent, onCleaned, validateCandidate) {
  const policy = offer.policy;
  const privateSession = session.fromPartition('nexus-login-' + offer.captureId + '-' + offer.nonce, { cache: false });
  const windows = new Set(); const observers = new Set(); const pending = new Set();
  const passive = Object.create(null);
  let root; let timer; let navigating = false; let sampling = false; let rejected = false; let closing = false; let cleanup; let settled = false; let bootstrapping = true;
  let lastCleanupFailure;
  privateSession.setPermissionRequestHandler(denyPermission);
  privateSession.setPermissionCheckHandler(denyCapability);
  privateSession.setDevicePermissionHandler(denyCapability);
  privateSession.on('will-download', denyDownload);
  const preferences = { session: privateSession, contextIsolation: true, sandbox: true, nodeIntegration: false,
    nodeIntegrationInSubFrames: false, webSecurity: true, webviewTag: false, devTools: false, spellcheck: false,
    disableDialogs: true, navigateOnDragDrop: false };
  const diagnostic = (stage, details) => recordDiagnostic('browser-login-stage', stage, details);
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
  async function clearSession(failures) {
    async function attempt(stage, action) {
      try { await action(); } catch (error) {
        if (failures.length < 8) failures.push({ stage, ...describeFailure(error) });
      }
    }
    for (const observer of observers) await attempt('observer', () => { observer.dispose(); observers.delete(observer); });
    await Promise.all([...windows].map(window => attempt('window', () => new Promise((resolve, reject) => {
      if (window.isDestroyed()) { windows.delete(window); resolve(); return; }
      const closed = () => { clearTimeout(timeout); resolve(); };
      const timeout = setTimeout(() => {
        window.removeListener('closed', closed); reject(new Error('browser_login_window_not_closed'));
      }, 5000);
      window.once('closed', closed);
      try { window.destroy(); } catch (error) { clearTimeout(timeout); window.removeListener('closed', closed); reject(error); }
    }))));
    if (windows.size !== 0) return;
    observers.clear();
    await Promise.allSettled([...pending]);
    await attempt('connections', () => privateSession.closeAllConnections());
    await attempt('storage', () => privateSession.clearStorageData());
    await attempt('cache', () => privateSession.clearCache());
    await attempt('authentication-cache', () => privateSession.clearAuthCache());
    await attempt('resolver-cache', () => privateSession.clearHostResolverCache());
    await attempt('cookies', async () => {
      if ((await privateSession.cookies.get({})).length !== 0) throw new Error('browser_login_storage_not_cleared');
    });
    if (failures.length === 0) {
      privateSession.webRequest.onBeforeRequest(null);
      privateSession.removeListener('will-download', denyDownload);
    }
    for (const key of Object.keys(passive)) delete passive[key];
  }
  async function finish(status, values = Object.create(null)) {
    if (settled) return;
    if (cleanup) return cleanup;
    if (!closing) diagnostic(status === 'canceled' ? 'canceled' : 'cleanup', { status });
    closing = true; clearTimeout(timer);
    cleanup = (async () => {
      const failures = [];
      try {
        await clearSession(failures);
        if (failures.length === 0) { await onCleaned(status, values); settled = true; }
      } catch (error) { failures.push({ stage: 'finalize', ...describeFailure(error) }); }
      finally {
        if (!settled) {
          const failure = JSON.stringify(failures);
          if (failure !== lastCleanupFailure) diagnostic('cleanup-failed', { primary: failures[0], cleanup: failures.slice(1) });
          lastCleanupFailure = failure; cleanup = undefined;
        }
        for (const key of Object.keys(values)) delete values[key];
      }
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
    } catch (error) {
      if (!closing) { rejected = true; diagnostic('validation-unavailable', describeFailure(error)); root?.setTitle(text('login') + ' · ' + text('loginRetry')); }
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
  void started.catch(error => { diagnostic('startup-failed', describeFailure(error)); return finish('failed'); });
  return Object.freeze({ cancel: () => finish('canceled'), retry: () => closing ? finish('failed') : Promise.resolve(), started });
}
module.exports = { createLoginWindow };
