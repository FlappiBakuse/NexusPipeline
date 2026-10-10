import {BrowserWindow,WebContentsView,ipcMain,IpcMainInvokeEvent,Session,WebContents,session} from 'electron';
import {BrowserLoginDescriptor} from '../shared/contracts';
import {Supervisor} from './supervisor';

interface LoginWindow {
  descriptor:BrowserLoginDescriptor;window:BrowserWindow;view:WebContentsView;session:Session;
  popups:Set<BrowserWindow>;busy:boolean;closing:boolean;timer:ReturnType<typeof setTimeout>;
  cleanup?:Promise<void>;
}
const bounded=<T>(promise:Promise<T>,ms:number)=>new Promise<T>((resolve,reject)=>{
  const timer=setTimeout(()=>reject(new Error('browser_step_timeout')),ms);
  promise.then(value=>{clearTimeout(timer);resolve(value);},()=>{clearTimeout(timer);reject(new Error('browser_step_failed'));});
});

export class BrowserLoginWindows {
  private readonly windows=new Map<string,LoginWindow>();
  constructor(private readonly supervisor:Supervisor,private readonly preload:string,private readonly locale:()=>string=()=> 'zh-CN') {
    ipcMain.handle('login.command',(event,value)=>this.command(event,value));
    supervisor.on('browser-cancel',(id:string)=>{const entry=this.windows.get(id);if(entry)void this.close(entry,false);});
  }
  async open(descriptor:BrowserLoginDescriptor):Promise<{operationId:string}> {
    const id=descriptor.operationId;
    if(!/^[0-9a-f]{32}$/.test(id)||!descriptor.flow.ready)throw new Error('browser_descriptor_invalid');
    const existing=this.windows.get(id);if(existing){existing.window.show();existing.window.focus();return {operationId:id};}
    if(this.windows.size)throw new Error('browser_operation_active');
    const isolated=session.fromPartition('nxp-login-'+id,{cache:false});
    isolated.setPermissionRequestHandler((_contents,_permission,callback)=>callback(false));
    isolated.setPermissionCheckHandler(()=>false);
    isolated.on('will-download',event=>event.preventDefault());
    const window=new BrowserWindow({title:'NexusPipeline Web',width:1080,height:800,minWidth:680,minHeight:480,show:false,autoHideMenuBar:true,
      webPreferences:{preload:this.preload,contextIsolation:true,sandbox:true,nodeIntegration:false,webviewTag:false}});
    const view=new WebContentsView({webPreferences:{session:isolated,contextIsolation:true,sandbox:true,nodeIntegration:false,webviewTag:false}});
    const entry:LoginWindow={descriptor,window,view,session:isolated,popups:new Set(),busy:false,closing:false,
      timer:setTimeout(()=>void this.close(entry,false),Math.max(1,Math.min(900000,Date.parse(descriptor.expiresAt)-Date.now())))};
    this.windows.set(id,entry);
    window.contentView.addChildView(view);
    const resize=()=>{const [width,height]=window.getContentSize();view.setBounds({x:0,y:72,width,height:Math.max(1,height-72)});};
    window.on('resize',resize);resize();
    window.on('close',event=>{if(!entry.closing){event.preventDefault();void this.close(entry,false);}});
    window.webContents.setWindowOpenHandler(()=>({action:'deny'}));
    window.webContents.on('will-navigate',(event,url)=>{if(url!==this.shellUrl(id))event.preventDefault();});
    window.webContents.on('render-process-gone',()=>void this.close(entry,false));
    this.secureContents(entry,view.webContents,false);
    view.webContents.on('did-navigate',()=>this.update(entry));
    view.webContents.on('did-navigate-in-page',()=>this.update(entry));
    view.webContents.on('did-finish-load',()=>this.update(entry));
    try {
      await window.loadURL(this.shellUrl(id));window.show();
      void view.webContents.loadURL(descriptor.flow.startUri).catch(()=>this.update(entry,'browser_navigation_failed'));
    } catch {await this.close(entry,false);throw new Error('browser_window_failed');}
    return {operationId:id};
  }
  private shellUrl(id:string){return 'nxp-desktop://login/'+id;}
  private allowed(entry:LoginWindow,url:string,popup:boolean):boolean {
    try{const target=new URL(url);return target.protocol==='https:'&&target.username===''&&target.password===''&&
      (popup?[...entry.descriptor.flow.navigationOrigins,...entry.descriptor.flow.popupOrigins]:entry.descriptor.flow.navigationOrigins).includes(target.origin);}catch{return false;}
  }
  private secureContents(entry:LoginWindow,contents:WebContents,popup:boolean) {
    contents.on('will-navigate',(event,url)=>{if(!this.allowed(entry,url,popup))event.preventDefault();});
    contents.on('will-redirect',(event,url)=>{if(!this.allowed(entry,url,popup))event.preventDefault();});
    contents.on('will-attach-webview',event=>event.preventDefault());
    contents.on('render-process-gone',()=>void this.close(entry,false));
    contents.setWindowOpenHandler(details=>{
      if(!this.allowed(entry,details.url,true)||!entry.descriptor.flow.popupOrigins.includes(new URL(details.url).origin))return {action:'deny'};
      return {action:'allow',overrideBrowserWindowOptions:{parent:entry.window,autoHideMenuBar:true,
        webPreferences:{session:entry.session,contextIsolation:true,sandbox:true,nodeIntegration:false,webviewTag:false,preload:undefined}}};
    });
    contents.on('did-create-window',popupWindow=>{
      entry.popups.add(popupWindow);this.secureContents(entry,popupWindow.webContents,true);
      popupWindow.on('closed',()=>entry.popups.delete(popupWindow));
    });
  }
  private update(entry:LoginWindow,error='') {
    if(entry.closing||entry.window.isDestroyed())return;
    const contents=entry.view.webContents;
    let origin='';try{origin=new URL(contents.getURL()).origin;}catch{}
    entry.window.webContents.send('login.state',{title:entry.descriptor.flow.title,origin,busy:entry.busy,error,
      back:contents.navigationHistory.canGoBack(),forward:contents.navigationHistory.canGoForward()});
  }
  private async command(event:IpcMainInvokeEvent,value:unknown):Promise<void> {
    if(!value||typeof value!=='object')throw new Error('login_sender_rejected');
    const request=value as {operationId?:unknown;action?:unknown};
    const entry=typeof request.operationId==='string'?this.windows.get(request.operationId):null;
    if(!entry||entry.closing||event.sender!==entry.window.webContents||event.senderFrame!==entry.window.webContents.mainFrame||event.senderFrame.url!==this.shellUrl(entry.descriptor.operationId))throw new Error('login_sender_rejected');
    if(request.action==='cancel'){await this.close(entry,false);return;}
    if(entry.busy)return;
    const external=entry.view.webContents;
    if(request.action==='back'){if(external.navigationHistory.canGoBack())external.navigationHistory.goBack();return;}
    if(request.action==='forward'){if(external.navigationHistory.canGoForward())external.navigationHistory.goForward();return;}
    if(request.action==='reload'){external.reload();return;}
    if(request.action!=='complete')throw new Error('login_action_invalid');
    entry.busy=true;this.update(entry);
    try {
      const capture=await bounded(this.capture(entry),10000);
      if(entry.closing)return;
      const result=await this.supervisor.request('browser.complete',{operationId:entry.descriptor.operationId,capture}) as {success:boolean;error?:string};
      if(entry.closing)return;
      if(!result.success){entry.busy=false;this.update(entry,result.error||'browser_validation_failed');return;}
      await this.close(entry,true);
    }catch{if(!entry.closing){entry.busy=false;this.update(entry,'browser_capture_failed');}}
  }
  private async capture(entry:LoginWindow) {
    const contents=entry.view.webContents;
    if(entry.closing||!this.allowed(entry,contents.getURL(),false))throw new Error('browser_origin_unverified');
    const frame=contents.mainFrame,origin=new URL(frame.url).origin;
    const cookies=[];
    for(const rule of entry.descriptor.flow.cookies) {
      const matches=(await entry.session.cookies.get({name:rule.name,domain:rule.domain})).filter(cookie=>cookie.domain===rule.domain&&cookie.path===rule.path);
      if(matches.length>1)throw new Error('browser_capture_ambiguous');
      if(matches.length)cookies.push({...rule,value:matches[0].value});
    }
    const storage=[];
    for(const rule of entry.descriptor.flow.storage) {
      if(rule.origin!==origin)throw new Error('browser_origin_unverified');
      const argument=JSON.stringify({storage:rule.storage,key:rule.key,jsonPath:rule.jsonPath});
      const value:unknown=await contents.executeJavaScriptInIsolatedWorld(999,[{code:`((r)=>{let v=window[r.storage].getItem(r.key);if(v===null)return null;if(v.length>65536)throw Error('limit');if(r.jsonPath.length){v=JSON.parse(v);for(const p of r.jsonPath){if(v===null||typeof v!=='object'||!Object.prototype.hasOwnProperty.call(v,p))return null;v=v[p];}}if(v!==null&&typeof v!=='string')throw Error('type');return v;})(${argument})`}]);
      if(value!==null&&typeof value!=='string')throw new Error('browser_capture_invalid');
      storage.push({origin:rule.origin,storage:rule.storage,key:rule.key,value});
    }
    if(entry.closing||contents.mainFrame!==frame||new URL(frame.url).origin!==origin)throw new Error('browser_origin_changed');
    const result={cookies,storage,capturedAt:new Date().toISOString()};
    if(Buffer.byteLength(JSON.stringify(result),'utf8')>48000)throw new Error('browser_capture_limit');
    return result;
  }
  private close(entry:LoginWindow,completed:boolean):Promise<void> {
    if(entry.cleanup)return entry.cleanup;
    entry.cleanup=this.cleanup(entry,completed);return entry.cleanup;
  }
  private async cleanup(entry:LoginWindow,completed:boolean) {
    entry.closing=true;clearTimeout(entry.timer);
    const contents=entry.view.webContents;
    let cleaned=true;
    try {
      contents.stop();for(const popup of entry.popups)if(!popup.isDestroyed())popup.destroy();entry.popups.clear();
      if(!contents.isDestroyed())contents.close();
      await bounded(Promise.all([entry.session.clearStorageData(),entry.session.clearCache(),entry.session.clearAuthCache(),entry.session.closeAllConnections()]),10000);
    }catch{cleaned=false;}
    finally {if(!contents.isDestroyed())contents.close();if(!entry.window.isDestroyed())entry.window.destroy();}
    try {
      if(completed)await this.supervisor.request('browser.cleaned',{operationId:entry.descriptor.operationId,cleaned});
      else await this.supervisor.request('browser.cancel',{operationId:entry.descriptor.operationId});
    }catch{}
    finally {this.windows.delete(entry.descriptor.operationId);}
  }
  async stop(){await Promise.all([...this.windows.values()].map(entry=>this.close(entry,false)));}
  document(id:string):string|null {
    if(!this.windows.has(id))return null;
    const english=this.locale()==='en-US',lang=english?'en-US':'zh-CN';
    const text=english?{title:'Web sign-in',back:'Back',forward:'Forward',reload:'Reload',cancel:'Cancel',complete:'Finish sign-in',failed:'Verification incomplete. Check sign-in and try again.',busy:'Verifying…'}:
      {title:'网页登录',back:'后退',forward:'前进',reload:'刷新',cancel:'取消',complete:'完成登录',failed:'尚未完成验证，请检查登录后重试。',busy:'正在验证，请稍候…'};
    return `<!doctype html><html lang="${lang}"><meta charset="utf-8"><title>NexusPipeline Web</title>
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-nxp-login'">
<style>body{margin:0;padding:12px 20px;background:#152033;color:#e6eefb;font:14px system-ui}header{display:flex;align-items:center;gap:16px;min-height:48px}.identity{display:flex;align-items:center;gap:12px;flex:1;min-width:0}strong{flex-shrink:0}.actions{display:flex;gap:8px;flex-shrink:0}button{min-height:40px;border:1px solid #40536c;border-radius:8px;padding:0 14px;background:transparent;color:inherit;cursor:pointer}button:disabled{opacity:.5;cursor:default}#complete{background:#376fe8;border-color:#376fe8}#error{margin:0;color:#ffc78b;font-size:12px;line-height:16px;overflow-wrap:anywhere;display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:3;overflow:hidden}@media(max-width:760px){body{padding-inline:12px}header{gap:8px}.identity{gap:8px}.actions{gap:6px}button{padding-inline:10px}}</style>
<header><div class="identity"><strong id="title">${text.title}</strong><p id="error" role="status"></p></div><div class="actions"><button data-action="back" aria-label="${text.back}">←</button><button data-action="forward" aria-label="${text.forward}">→</button><button data-action="reload">${text.reload}</button><button data-action="cancel">${text.cancel}</button><button id="complete" data-action="complete">${text.complete}</button></div></header>
<script nonce="nxp-login">
const op=${JSON.stringify(id)},text=${JSON.stringify(text)};
document.querySelectorAll('button').forEach(b=>b.onclick=()=>window.loginShell.command(op,b.dataset.action));
window.loginShell.onState(s=>{
  document.getElementById('title').textContent=s.title;
  const message=s.error?text.failed+' ('+s.error+')':s.busy?text.busy:'';
  document.getElementById('error').textContent=message;
  document.getElementById('error').title=message;
  document.querySelectorAll('button').forEach(b=>b.disabled=b.dataset.action==='cancel'?false:s.busy||b.dataset.action==='back'&&!s.back||b.dataset.action==='forward'&&!s.forward);
});
</script></html>`;
  }
}
