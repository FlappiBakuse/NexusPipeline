'use strict';
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { text } = require('./native-text.cjs');

const shellUrl = pathToFileURL(path.join(__dirname, 'login-shell.html')).href;
const shellPreferences = { preload: path.join(__dirname, 'login-shell-preload.cjs'), contextIsolation: true,
  sandbox: true, nodeIntegration: false, nodeIntegrationInSubFrames: false, webSecurity: true, webviewTag: false,
  devTools: false, spellcheck: false, navigateOnDragDrop: false };
const labelKeys = { title: 'loginWeb', toolbar: 'loginActions', navigation: 'loginNavigation', finish: 'loginFinishActions',
  back: 'loginBack', forward: 'loginForward', reload: 'loginRefresh', cancel: 'cancel', complete: 'loginComplete' };

function configureLoginShell(window, command, getState) {
  const contents = window.webContents;
  contents.setWindowOpenHandler(() => ({ action: 'deny' }));
  contents.on('will-frame-navigate', event => event.preventDefault());
  contents.on('will-redirect', event => event.preventDefault());
  contents.on('will-attach-webview', event => event.preventDefault());
  contents.ipc.handle('login.action', (event, action) => {
    if (event.sender !== contents || event.senderFrame !== contents.mainFrame || event.senderFrame.url !== shellUrl)
      throw new Error('login_shell_source_denied');
    if (action === 'state') return { ...getState(), labels: Object.fromEntries(Object.entries(labelKeys).map(([key, value]) => [key, text(value)])) };
    return command(action);
  });
  return {
    load: () => contents.loadURL(shellUrl),
    update: () => { if (!contents.isDestroyed() && contents.getURL() === shellUrl) contents.send('login.state', getState()); }
  };
}
module.exports = { configureLoginShell, shellPreferences };
