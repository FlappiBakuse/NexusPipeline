'use strict';
const electron = require('electron');
const { app, BrowserWindow, Menu, dialog, screen, ipcMain } = electron;
const path = require('node:path');
const { connectStartup } = require('./startup-channel.cjs');
const { createStartupView } = require('./startup-view.cjs');
const { strictJson } = require('./strict-json.cjs');
const { installNativeBridge } = require('./native-bridge.cjs');
const { installFilePermissions } = require('./file-permissions.cjs');
const { loadHostPage } = require('./host-page.cjs');
const { createTrayLifecycle } = require('./tray-lifecycle.cjs');
const { setLocale, text } = require('./native-text.cjs');
const { chooseWindowPlacement, MIN_WIDTH, MIN_HEIGHT, CAPTION_HEIGHT } = require('./window-placement.cjs');
const { createWindowState } = require('./window-state.cjs');

const argument = process.argv.find(value => value.startsWith('--nexus-bootstrap='));
if (!argument || argument.length > 8192) throw new Error('desktop_bootstrap_required');
const config = strictJson(Buffer.from(argument.slice('--nexus-bootstrap='.length), 'base64').toString('utf8'));
const fields = ['startupPipe', 'nonce', 'locale', 'instanceId', 'verificationKey', 'profilePath', 'visible', 'route', 'minimizeToTray', 'mode'];
if (Object.keys(config).length !== fields.length || fields.some(name => !Object.hasOwn(config, name)) ||
    !path.isAbsolute(config.profilePath) || typeof config.visible !== 'boolean' || typeof config.minimizeToTray !== 'boolean' ||
    !['desktop', 'browser'].includes(config.mode) || typeof config.route !== 'string' ||
    !/^\/(?!\/)[^\\?#%:\x00-\x1f]{0,255}$/.test(config.route)) throw new Error('desktop_bootstrap_invalid');
if (!['zh-CN', 'en-US'].includes(config.locale)) throw new Error('desktop_locale_invalid');
setLocale(config.locale);
const startup = connectStartup(config);
let starting = true, overlay, page;
let presented;
const firstPage = new Promise(resolve => { presented = resolve; });
app.setPath('userData', config.profilePath);
app.setPath('sessionData', path.join(config.profilePath, 'session'));
app.setPath('crashDumps', path.join(config.profilePath, 'dumps'));
app.setAppUserModelId('NexusPipeline.Desktop');
let window;
let tray;
let bridge;
let quitting = false;
process.on('uncaughtException', () => app.exit(1));
app.on('window-all-closed', () => { if (quitting) app.quit(); });
// Bound transient renderer allocation without limiting the retained application heap.
app.commandLine.appendSwitch('js-flags', '--max-semi-space-size=1');
app.whenReady().then(async () => {
  Menu.setApplicationMenu(null);
  const windowState = createWindowState(config.profilePath, screen);
  let loadedPage = false;
  async function createWindow() {
    if (window && !window.isDestroyed()) return window;
    const created = new BrowserWindow({ width: 1280, height: 760, minWidth: 1280, minHeight: 760,
      useContentSize: true, frame: false, icon: path.join(__dirname, 'nexuspipeline.ico'), title: 'NexusPipeline', show: false, backgroundColor: '#131f33',
      webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, sandbox: true,
        nodeIntegration: false, webSecurity: true, spellcheck: false, backgroundThrottling: true } });
    window = created;
    windowState.attach(created);
    created.webContents.on('will-attach-webview', event => event.preventDefault());
    created.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    created.webContents.on('will-navigate', (event, url) => { if (url.split('#')[0] !== page) event.preventDefault(); });
    if (page) installFilePermissions(created, page, dialog);
    bridge?.attachWindow(created);
    created.on('close', event => {
      windowState.flush();
      if (quitting) return;
      event.preventDefault();
      if (starting || config.minimizeToTray) created.hide();
      else bridge.requestClose().catch(() => {});
    });
    created.on('closed', () => { if (window === created) window = undefined; });
    if (starting) {
      overlay = createStartupView(created, startup, config);
      await overlay.loaded;
    } else await loadHostPage(created, page);
    return created;
  }
  async function showWindow() {
    const current = await createWindow();
    const outer = current.getBounds(), content = current.getContentBounds();
    const placement = chooseWindowPlacement({ displays: screen.getAllDisplays(), primaryDisplayId: screen.getPrimaryDisplay().id,
      saved: windowState.saved, frameInsets: { width: Math.max(0, outer.width - content.width), height: Math.max(0, outer.height - content.height) } });
    if (placement.kind === 'low-resolution') {
      const answer = await dialog.showMessageBox({ type: 'warning', title: 'NexusPipeline',
        message: text('smallWorkArea'), detail: text('smallWorkAreaDetail'),
        buttons: [text('continueOpen'), text('backgroundOnly'), text('cancelOpen')], defaultId: 1, cancelId: 2, noLink: true });
      if (answer.response !== 0) { current.hide(); return; }
      current.unmaximize(); current.setContentSize(MIN_WIDTH, MIN_HEIGHT + CAPTION_HEIGHT);
      current.setPosition(placement.area.x, placement.area.y);
    } else if (placement.kind === 'show') {
      if (current.isMaximized()) current.unmaximize();
      current.setBounds(placement.bounds);
      if (placement.maximized) current.maximize();
    }
    if (current.isMinimized()) current.restore();
    current.show(); current.focus();
  }
  const displayChanged = () => {
    if (!window || window.isDestroyed() || !window.isVisible()) return;
    const placement = chooseWindowPlacement({ displays: screen.getAllDisplays(),
      primaryDisplayId: screen.getPrimaryDisplay().id, saved: windowState.saved });
    if (placement.kind === 'show') {
      const maximized = window.isMaximized();
      if (maximized) window.unmaximize();
      window.setBounds(placement.bounds);
      if (maximized) window.maximize();
    } else if (placement.kind === 'low-resolution') window.setPosition(placement.area.x, placement.area.y);
  };
  screen.on('display-removed', displayChanged);
  screen.on('display-metrics-changed', displayChanged);
  const windows = {
    show: showWindow,
    reload: async () => { if (window) await loadHostPage(window, page); else await createWindow(); },
    release: () => { windowState.flush(); window?.destroy(); }
  };
  const lifecycle = {
    requestClose: () => bridge.requestClose(),
    changeWindow: action => bridge.changeWindow(action),
    publishMode: mode => bridge.publishMode(mode)
  };
  tray = createTrayLifecycle(config, lifecycle, windows, electron, startup);
  await tray.start();
  startup.subscribe(value => {
    if (value.kind === 'activate') void tray.show().catch(() => {});
  });
  if (startup.activated) await tray.show();
  ipcMain.handle('desktop.preparation', (event, value) => {
    if (!window || event.sender !== window.webContents || event.senderFrame !== window.webContents.mainFrame ||
        event.senderFrame.url.split('#')[0] !== page) throw new Error('desktop_source_denied');
    if (!value || !['准备界面', '加载插件界面', '准备页面数据', '准备首次显示'].includes(value.stage) ||
        !Number.isInteger(value.done) || !Number.isInteger(value.total) || value.done < 0 ||
        value.done > value.total || value.total > 256) throw new Error('desktop_preparation_invalid');
    overlay?.preparation(value);
  });
  ipcMain.handle('desktop.presented', event => {
    if (!window || event.sender !== window.webContents || event.senderFrame !== window.webContents.mainFrame ||
        event.senderFrame.url.split('#')[0] !== page) throw new Error('desktop_source_denied');
    presented();
  });
  try {
    const ready = await startup.ready;
    const address = new URL(ready.address);
    if (address.protocol !== 'http:' || address.hostname !== '127.0.0.1' || !address.port || address.username ||
        address.password || address.pathname !== '/' || address.search || address.hash) throw new Error('desktop_host_denied');
    Object.assign(config, ready);
    const preparedAt = performance.now();
    const measure = stage => startup.measure(stage, performance.now() - preparedAt);
    page = new URL('/', address).href;
    bridge = installNativeBridge(window, config, page, async () => {
      quitting = true; windowState.dispose();
      screen.removeListener('display-removed', displayChanged); screen.removeListener('display-metrics-changed', displayChanged);
      tray?.dispose(); overlay?.dispose(); startup.dispose(); await bridge.dispose(); window?.destroy(); app.quit();
    }, locale => { config.locale = locale; setLocale(locale); tray?.refreshMenu(); });
    if (window) {
      installFilePermissions(window, page, dialog);
      await loadHostPage(window, page); loadedPage = true;
      measure('document');
    }
    await bridge.start();
    measure('session');
    if (window) {
      await Promise.all([firstPage, startup.complete]);
      measure('interface');
      overlay?.dispose(); overlay = undefined;
      startup.presented('console');
    }
    await startup.complete;
    starting = false;
    await tray.complete();
    if (!loadedPage) startup.presented('tray');
  } catch (error) {
    if (!startup.failed) throw error;
  }
}).catch(error => { console.error('desktop_startup_failed', String(error?.message).slice(0, 160)); app.exit(1); });
