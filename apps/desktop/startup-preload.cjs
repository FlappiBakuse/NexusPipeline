'use strict';
const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('nexusStartup', Object.freeze({
  action: action => ipcRenderer.invoke('startup.action', action),
  subscribe: callback => ipcRenderer.on('startup.state', (_event, state) => callback(state))
}));
