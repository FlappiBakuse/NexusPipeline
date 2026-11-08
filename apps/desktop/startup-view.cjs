'use strict';
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { WebContentsView, ipcMain, clipboard } = require('electron');

function createStartupView(window, channel, config) {
  const view = new WebContentsView({ webPreferences: { preload: path.join(__dirname, 'startup-preload.cjs'),
    contextIsolation: true, sandbox: true, nodeIntegration: false, webSecurity: true, spellcheck: false } });
  const page = pathToFileURL(path.join(__dirname, 'startup.html')).href;
  window.contentView.addChildView(view);
  const resize = () => { const [width, height] = window.getContentSize(); view.setBounds({ x: 0, y: 0, width, height }); };
  resize(); window.on('resize', resize);
  view.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  view.webContents.on('will-navigate', event => event.preventDefault());
  const update = value => {
    if (value.kind !== 'activate' && !view.webContents.isDestroyed())
      view.webContents.send('startup.state', { ...value, locale: config.locale });
  };
  const unsubscribe = channel.subscribe(update);
  const valid = event => event.sender === view.webContents && event.senderFrame === view.webContents.mainFrame && event.senderFrame.url === page;
  ipcMain.handle('startup.action', (event, action) => {
    if (!valid(event)) throw new Error('startup_view_source_denied');
    if (action === 'state') return { ...channel.snapshot, locale: config.locale };
    if (action === 'hide') return window.hide();
    if (action === 'minimize') return window.minimize();
    if (action === 'maximize') return window.isMaximized() ? window.unmaximize() : window.maximize();
    if (channel.failed && action === 'copy') return clipboard.writeText(JSON.stringify(channel.snapshot, null, 2));
    if (channel.failed && action === 'exit') { channel.dismiss(); return; }
    throw new Error('startup_view_action_denied');
  });
  const loaded = view.webContents.loadURL(page).then(() => channel.presented('progress'));
  return { loaded, preparation(value) { update({ ...channel.snapshot, preparation: value }); }, dispose() {
    unsubscribe(); window.removeListener('resize', resize); ipcMain.removeHandler('startup.action');
    if (!window.isDestroyed()) window.contentView.removeChildView(view);
    if (!view.webContents.isDestroyed()) view.webContents.close();
  } };
}
module.exports = { createStartupView };
