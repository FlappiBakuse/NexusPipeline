import {contextBridge,ipcRenderer} from "electron";
import type {NexusDesktopBridge} from "../shared/contracts";
function subscribe<T>(channel: string,listener: (value: T)=>void) {
  if(typeof listener!=="function") throw new TypeError("Listener required");
  const handler=(_event: unknown,value: T)=>listener(value);
  ipcRenderer.on(channel,handler);
  ipcRenderer.send("desktop.subscribe",channel);
  let active=true;
  return ()=>{if(active){active=false;ipcRenderer.removeListener(channel,handler);}};
}
const bridge: NexusDesktopBridge={
  bootstrapProtocolVersion:1 as const,
  getWindowState:()=>ipcRenderer.invoke("desktop.window-state"),
  minimize:()=>ipcRenderer.invoke("desktop.minimize"),
  toggleMaximize:()=>ipcRenderer.invoke("desktop.toggle-maximize"),
  hideWindow:()=>ipcRenderer.invoke("desktop.hide"),
  getClientPreferences:()=>ipcRenderer.invoke("desktop.preferences"),
  setClientPreferences:(patch)=>ipcRenderer.invoke("desktop.set-preferences",patch),
  writeClipboardText:(text)=>ipcRenderer.invoke("desktop.write-clipboard-text",text),
  openExternal:(url)=>ipcRenderer.invoke("desktop.open-external",url),
  getClientSessionToken:()=>ipcRenderer.invoke("desktop.client-session"),
  openBrowserLogin:(request)=>ipcRenderer.invoke("desktop.browser-login",request),
  onWindowStateChanged:(listener)=>subscribe("desktop.window-state-changed",listener),
  onConnectionStateChanged:(listener)=>subscribe("desktop.connection-state",listener),
  onConnectionCandidate:(listener)=>subscribe("desktop.connection-candidate",listener),
  confirmConnectionNavigation:(id)=>ipcRenderer.invoke("desktop.confirm-navigation",id),
  onShutdownNotice:(listener)=>subscribe("desktop.shutdown-notice",listener),
  onPageCommand:(listener)=>subscribe("desktop.page-command",listener),
  reportPageCommand:(requestId,result)=>ipcRenderer.send("desktop.page-command-result",{requestId,result}),
};
contextBridge.exposeInMainWorld("nexusDesktop",Object.freeze(bridge));
