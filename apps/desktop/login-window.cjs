'use strict';
const { BrowserWindow, WebContentsView, session } = require('electron');
const { getLocale, text } = require('./native-text.cjs');
const { isReady, collectValues, watchResponses, origin } = require('./login-values.cjs');
const { describeFailure, recordDiagnostic } = require('./diagnostics.cjs');
const { configureLoginShell, shellPreferences } = require('./login-shell.cjs');
const { createLoginPageActions } = require('./login-page-actions.cjs');
const denyPermission = (_contents, _permission, answer) => answer(false);
const denyCapability = () => false;
const denyDownload = event => event.preventDefault();

function createLoginWindow(offer, parent, onCleaned, validateCandidate, theme) {
  const policy = offer.policy;
  const privateSession = session.fromPartition('nexus-login-' + offer.captureId + '-' + offer.nonce, { cache: false });
  const windows = new Set(); const pages = new Set(); const observers = new Set(); const pending = new Set();
  const passive = Object.create(null);
  let root; let primary; let shell; let stopPageActions;
  let navigating = false; let sampling = false; let closing = false; let cleanup; let settled = false; let canceled = false;
  let bootstrapping = true; let ready = false; let documentGeneration = 0; let lastCleanupFailure;
  let address = policy.startUrl; let status = 'loginLoading';
  privateSession.setPermissionRequestHandler(denyPermission);
  privateSession.setPermissionCheckHandler(denyCapability);
  privateSession.setDevicePermissionHandler(denyCapability);
  privateSession.on('will-download', denyDownload);
  const preferences = { session: privateSession, contextIsolation: true, sandbox: true, nodeIntegration: false,
    nodeIntegrationInSubFrames: false, webSecurity: true, webviewTag: false, devTools: false, spellcheck: false,
    disableDialogs: true, navigateOnDragDrop: false };
  const diagnostic = (stage, details) => recordDiagnostic('browser-login-stage', stage, details);
  function allowed(url) { return policy.origins.includes(origin(url)); }
  function watchRenderer(contents, surface) {
    contents.on('render-process-gone', (_event, { reason, exitCode }) => {
      if (closing) return;
      diagnostic('renderer-exited', { surface, reason, exitCode });
      void finish('failed');
    });
    contents.on('unresponsive', () => { if (!closing) diagnostic('renderer-unresponsive', { surface }); });
  }
  privateSession.webRequest.onBeforeRequest({ urls: ['<all_urls>'] }, (details, answer) => {
    const bootstrap = details.url === 'about:blank' && bootstrapping && !closing;
    const denied = ['mainFrame', 'subFrame'].includes(details.resourceType) && !bootstrap && !allowed(details.url);
    if (denied) diagnostic('frame-rejected', { category: details.resourceType });
    answer({ cancel: denied });
  });
  function configure(contents) {
    pages.add(contents);
    contents.once('destroyed', () => pages.delete(contents));
    contents.on('did-fail-load', (_event, code, _description, _url, mainFrame) => diagnostic('load-failed', { code, mainFrame }));
    contents.on('did-finish-load', () => diagnostic('loaded'));
    contents.on('dom-ready', () => diagnostic('dom-ready'));
    contents.on('will-attach-webview', event => event.preventDefault());
    contents.on('will-frame-navigate', event => { if (!allowed(event.url)) event.preventDefault(); });
    contents.on('will-redirect', (event, url) => { if (!allowed(url)) event.preventDefault(); });
    watchRenderer(contents, 'official');
    contents.setWindowOpenHandler(details => {
      if (closing || windows.size >= 4 || !policy.popupOrigins.includes(origin(details.url))) return { action: 'deny' };
      return { action: 'allow', overrideBrowserWindowOptions: { parent: root, autoHideMenuBar: true, webPreferences: preferences } };
    });
    contents.on('did-create-window', popup => {
      if (closing || windows.size >= 4 || !allowed(popup.webContents.getURL()) && popup.webContents.getURL() !== '') { popup.destroy(); return; }
      windows.add(popup); popup.setMenu(null);
      popup.once('closed', () => windows.delete(popup));
      configure(popup.webContents);
    });
    const observer = watchResponses(contents, policy, passive, pending, () => closing, () => { if (!closing) void finish('failed'); });
    observers.add(observer); return observer.ready;
  }
  function closeTarget(target, event, close, error) {
    return new Promise((resolve, reject) => {
      if (!target || target.isDestroyed()) { resolve(); return; }
      const done = () => { clearTimeout(timeout); resolve(); };
      const timeout = setTimeout(() => {
        target.removeListener(event, done); reject(new Error(error));
      }, 5000);
      target.once(event, done);
      try { close(); } catch (error) { clearTimeout(timeout); target.removeListener(event, done); reject(error); }
    });
  }
  async function clearSession(failures) {
    async function attempt(stage, action) {
      try { await action(); } catch (error) {
        if (failures.length < 8) failures.push({ stage, ...describeFailure(error) });
      }
    }
    for (const observer of observers) await attempt('observer', () => { observer.dispose(); observers.delete(observer); });
    await Promise.all([...pages].map(contents => attempt('view', () =>
      closeTarget(contents, 'destroyed', () => contents.close({ waitForBeforeUnload: false }), 'browser_login_view_not_closed'))));
    await Promise.all([...windows].map(window => attempt('window', () =>
      closeTarget(window, 'closed', () => window.destroy(), 'browser_login_window_not_closed'))));
    if (windows.size || pages.size) {
      if (!failures.length) failures.push({ stage: 'window', reason: 'browser_login_window_not_closed' });
      return;
    }
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
    if (!closing) {
      canceled = status === 'canceled';
      diagnostic(canceled ? 'canceled' : 'cleanup', { status });
    }
    closing = true; stopPageActions?.();
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
  function historyIndex(delta) {
    const history = primary.navigationHistory;
    const index = history.getActiveIndex() + delta;
    const entry = history.getEntryAtIndex(index);
    return entry && allowed(entry.url) ? index : undefined;
  }
  function getState() {
    const enabled = ready && !closing && !sampling;
    return { locale: getLocale(), theme, status: status ? text(status) : '',
      enabled: { back: enabled && !navigating && historyIndex(-1) !== undefined,
        forward: enabled && !navigating && historyIndex(1) !== undefined, reload: enabled,
        complete: enabled && !navigating && status !== 'loginLoadFailed' && allowed(primary.getURL()), cancel: !closing } };
  }
  function showStatus(value) { status = value; shell?.update(); }

  async function sample() {
    sampling = true; showStatus('loginVerifying');
    const current = documentGeneration;
    let values;
    try {
      await stopPageActions()?.catch(() => {});
      if (closing || current !== documentGeneration) return;
      values = await collectValues(policy, privateSession, pages, passive);
      if (closing || current !== documentGeneration) return;
      if (!isReady(policy, values)) { showStatus('loginIncomplete'); return; }
      diagnostic('validating');
      const accepted = await validateCandidate(values);
      diagnostic(accepted ? 'validation-accepted' : 'validation-rejected');
      if (closing) return;
      if (accepted) { await finish('completed', values); return; }
      showStatus('loginRejected');
    } catch (error) {
      if (!closing) { diagnostic('validation-unavailable', describeFailure(error)); showStatus('loginRetry'); }
    } finally {
      sampling = false;
      if (values) for (const key of Object.keys(values)) delete values[key];
      if (!closing) shell.update();
    }
  }
  function loadOfficial() {
    void primary.loadURL(address).catch(() => {});
  }
  function command(action) {
    const { enabled } = getState();
    if (!Object.hasOwn(enabled, action) || typeof action !== 'string') throw new Error('login_shell_action_denied');
    if (!enabled[action]) return;
    if (action === 'cancel') { void finish('canceled'); return; }
    if (action === 'complete') { void sample(); return; }
    if (action === 'reload') {
      diagnostic('refresh');
      if (allowed(primary.getURL()) && status !== 'loginLoadFailed') primary.reload();
      else loadOfficial();
      return;
    }
    primary.navigationHistory.goToIndex(historyIndex(action === 'back' ? -1 : 1));
  }
  function trackNavigation() {
    primary.on('did-start-navigation', event => {
      if (!ready || !event.isMainFrame || event.isSameDocument || !allowed(event.url)) return;
      address = event.url; documentGeneration++; navigating = true;
      diagnostic('navigating'); showStatus('loginLoading');
    });
    primary.on('did-navigate-in-page', (_event, _url, isMainFrame) => {
      if (isMainFrame) shell.update();
    });
    primary.on('did-stop-loading', () => {
      if (!ready || closing) return;
      navigating = false;
      showStatus(status === 'loginLoading' ? '' : status);
    });
    primary.on('did-fail-load', (_event, code, _description, _url, mainFrame) => {
      if (!ready || closing || !mainFrame || code === -3) return;
      diagnostic('navigation-unavailable', { code });
      navigating = false; showStatus('loginLoadFailed');
    });
  }
  const started = (async () => {
    root = new BrowserWindow({ width: 1080, height: 800, minWidth: 680, minHeight: 480, title: text('loginWeb'),
      show: false, autoHideMenuBar: true, ...(parent && !parent.isDestroyed() ? { parent } : {}), webPreferences: shellPreferences });
    windows.add(root); root.setMenu(null);
    root.once('closed', () => windows.delete(root));
    root.on('close', event => { if (!closing) { event.preventDefault(); void finish('canceled'); } });
    watchRenderer(root.webContents, 'shell');
    const view = new WebContentsView({ webPreferences: preferences });
    primary = view.webContents;
    root.contentView.addChildView(view);
    let layout; let lastWidth; let lastHeight;
    const resize = () => {
      if (closing || root.isDestroyed() || primary.isDestroyed()) return;
      const [width, height] = root.getContentSize();
      if (width < 1 || height <= 84 || width === lastWidth && height === lastHeight) return;
      view.setBounds({ x: 0, y: 84, width, height: height - 84 });
      lastWidth = width; lastHeight = height;
    };
    const scheduleResize = () => {
      if (closing || layout !== undefined) return;
      layout = setImmediate(() => { layout = undefined; resize(); });
    };
    resize();
    for (const event of ['resize', 'maximize', 'unmaximize', 'restore']) root.on(event, scheduleResize);
    root.once('closed', () => clearImmediate(layout));
    shell = configureLoginShell(root, command, getState);
    stopPageActions = createLoginPageActions(primary, policy, () => ready && !sampling && !closing);
    trackNavigation();
    diagnostic('window-created');
    diagnostic('capture-initializing');
    const initialized = configure(primary);
    await Promise.all([shell.load(), primary.loadURL('about:blank'), initialized]);
    bootstrapping = false;
    if (closing) return;
    diagnostic('cdp-attached'); ready = true;
    root.show(); loadOfficial();
  })();
  void started.catch(error => { diagnostic('startup-failed', describeFailure(error)); return finish('failed'); });
  return Object.freeze({ cancel: () => finish('canceled'), retry: () => closing ? finish(canceled ? 'canceled' : 'failed') : Promise.resolve(), started });
}
module.exports = { createLoginWindow };
