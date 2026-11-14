'use strict';
const { contextBridge, ipcRenderer } = require('electron');
if (process.isMainFrame) {
  let sessionId;
  let prepareExit;
  let exiting;
  const reportExit = (stage, token) => console.info(JSON.stringify({ kind: 'desktop.exit', stage, correlationId: token }));
  function cancelExit(current) {
    if (!current || current.cancelled) return;
    current.cancelled = true;
    reportExit('cancelled', current.token);
  }
  ipcRenderer.on('desktop.cancel-exit', (_event, token) => {
    if (exiting?.token === token) cancelExit(exiting);
  });
  ipcRenderer.on('desktop.prepare-exit', async (_event, token, reason) => {
    cancelExit(exiting);
    const current = { token, cancelled: false };
    exiting = current;
    reportExit('received', token);
    let readiness = 'pending-writes';
    try {
      try {
        if (['close', 'maintenance'].includes(reason) && prepareExit)
          readiness = await prepareExit(reason) === 'ready' ? 'ready' : 'pending-writes';
      }
      catch { reportExit('preparation-unavailable', token); }
      if (!current.cancelled) {
        const applied = await ipcRenderer.invoke('desktop.prepared', token, readiness);
        reportExit(applied === false ? 'retired' : readiness === 'ready' ? 'prepared' : 'pending-writes', token);
      }
    } catch { reportExit('reply-unavailable', token); }
    finally {
      if (exiting === current) exiting = undefined;
    }
  });
  const action = (type, url) => ipcRenderer.invoke('desktop.action', { type, sessionId, ...(url === undefined ? {} : { url }) });
  contextBridge.exposeInMainWorld('nexusDesktopChannel', Object.freeze({
    version: 1,
    presented: () => ipcRenderer.invoke('desktop.presented'),
    preparation: value => ipcRenderer.invoke('desktop.preparation', value),
    connect: async () => { const session = await ipcRenderer.invoke('desktop.connect'); sessionId = session.sessionId; return session; },
    bindFrontend: async (clientId, token) => {
      if (!sessionId) { const session = await ipcRenderer.invoke('desktop.connect'); sessionId = session.sessionId; }
      return await ipcRenderer.invoke('desktop.frontend.bind', { clientId, token, sessionId });
    },
    retireFrontend: clientId => ipcRenderer.invoke('desktop.frontend.retire', { clientId }),
    prepareExit: callback => { if (typeof callback !== 'function') throw new Error('desktop_preparation_callback_required'); prepareExit = callback; },
    onExitState: callback => {
      if (typeof callback !== 'function') throw new Error('desktop_exit_callback_required');
      const listener = (_event, state) => callback(state);
      ipcRenderer.on('desktop.exit-state', listener);
      return () => ipcRenderer.removeListener('desktop.exit-state', listener);
    },
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
