import {contextBridge,ipcRenderer} from 'electron';
contextBridge.exposeInMainWorld('loginShell',Object.freeze({
  command:(operationId:string,action:string)=>ipcRenderer.invoke('login.command',{operationId,action}),
  onState:(listener:(value:unknown)=>void)=>{ipcRenderer.on('login.state',(_event,value)=>listener(value));},
}));
