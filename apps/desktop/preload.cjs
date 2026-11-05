'use strict';
const { contextBridge, ipcRenderer } = require('electron');
if (process.isMainFrame) {
  let sessionId;
  let prepareExit;
  ipcRenderer.on('desktop.prepare-exit', async (_event, token, reason) => {
    let accepted = false;
    try { accepted = ['close', 'maintenance'].includes(reason) && prepareExit !== undefined && await prepareExit(reason) === true; } catch { }
    await ipcRenderer.invoke('desktop.prepared', token, accepted);
  });
  const action = (type, url) => ipcRenderer.invoke('desktop.action', { type, sessionId, ...(url === undefined ? {} : { url }) });
  contextBridge.exposeInMainWorld('nexusDesktopChannel', Object.freeze({
    version: 1,
    connect: async () => { const session = await ipcRenderer.invoke('desktop.connect'); sessionId = session.sessionId; return session; },
    bindFrontend: async (clientId, token) => {
      if (!sessionId) { const session = await ipcRenderer.invoke('desktop.connect'); sessionId = session.sessionId; }
      return await ipcRenderer.invoke('desktop.frontend.bind', { clientId, token, sessionId });
    },
    retireFrontend: clientId => ipcRenderer.invoke('desktop.frontend.retire', { clientId }),
    prepareExit: callback => { if (typeof callback !== 'function') throw new Error('desktop_preparation_callback_required'); prepareExit = callback; },
    setLocale: locale => ipcRenderer.invoke('desktop.locale', locale),
    chooseFile: () => action('file.select'), chooseFolder: () => action('folder.select'),
    openExternal: url => action('external.open', url), hide: () => action('hide'), show: () => action('show'),
    minimize: () => action('window.minimize'), toggleMaximize: () => action('window.maximize'),
    close: () => action('window.close'), windowState: () => action('window.state'),
    onWindowState: callback => {
      if (typeof callback !== 'function') throw new Error('desktop_window_callback_required');
      const listener = (_event, state) => callback(state);
      ipcRenderer.on('desktop.window-state', listener);
      return () => ipcRenderer.removeListener('desktop.window-state', listener);
    },
    quit: () => action('close.request')
  }));
}
