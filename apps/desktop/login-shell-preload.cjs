'use strict';
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('loginShell', Object.freeze({
  action: action => ipcRenderer.invoke('login.action', action),
  onState: listener => ipcRenderer.on('login.state', (_event, value) => listener(value))
}));
