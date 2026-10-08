import {app,dialog,ipcMain,protocol,screen,Menu,nativeTheme,clipboard,IpcMainInvokeEvent,IpcMainEvent,WebFrameMain} from "electron";
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import {Preferences} from "./client-preferences";
import {readBuild,verifyHttp,HostState} from "./host-identity";
import {Supervisor} from "./supervisor";
import {WindowManager} from "./window-manager";
import {ConnectionCandidate,ConnectionState} from "../shared/contracts";
import {runtimeLocale} from "../shared/runtime-locale";
import {PageCommands} from './page-commands';
app.commandLine.appendSwitch('lang', runtimeLocale(app.getPreferredSystemLanguages()[0] ?? ''));
protocol.registerSchemesAsPrivileged([{scheme:'nxp-desktop',privileges:{standard:true,secure:true,supportFetchAPI:true}}]);
async function start() {
  const argumentsMap=new Map<string,string>();
  const supplied=process.argv.slice(app.isPackaged?1:2);
  const allowed=['--supervisor-pipe','--desktop-session','--install-root-hash','--launch-intent'];
  if(supplied.length!==8)throw new Error('desktop_launch_ticket_required');
  for(let i=0;i<supplied.length;i+=2){if(!allowed.includes(supplied[i])||argumentsMap.has(supplied[i]))throw new Error('Invalid desktop launch arguments');argumentsMap.set(supplied[i],supplied[i+1]);}
  const sessionId=argumentsMap.get('--desktop-session')!,rootHash=argumentsMap.get('--install-root-hash')!,intent=argumentsMap.get('--launch-intent');
  const keyText=process.env.NEXUS_DESKTOP_SESSION_KEY;delete process.env.NEXUS_DESKTOP_SESSION_KEY;
  if(!/^[0-9a-f]{32}$/.test(sessionId)||!/^[0-9a-f]{64}$/.test(rootHash)||!keyText||!/^[0-9a-f]{64}$/.test(keyText)||!['show','background'].includes(intent!))throw new Error('desktop_launch_ticket_required');
  const root=path.resolve(path.dirname(process.execPath),'..','..');
  const expected=crypto.createHash('sha256').update(root.replace(/[\\/]+$/,'').toUpperCase(),'utf8').digest('hex');
  if(rootHash!==expected||path.basename(process.execPath)!=='NexusPipeline.Desktop.exe')throw new Error('Invalid desktop installation root');
  const build=readBuild(path.join(app.getAppPath(),'desktop-build.json'));
  const stateDir=path.join(root,'.nxp','state','desktop');
  for(const directory of [stateDir,path.join(stateDir,'profile'),path.join(stateDir,'browser-cache')]) {
    for(let current=directory;path.dirname(current)!==current;current=path.dirname(current)) if(fs.existsSync(current)&&fs.lstatSync(current).isSymbolicLink())throw new Error('Linked desktop state');
    fs.mkdirSync(directory,{recursive:true});
  }
  app.setPath('userData',path.join(stateDir,'profile'));app.setPath('sessionData',path.join(stateDir,'browser-cache'));
  if(!app.requestSingleInstanceLock({rootHash,sessionId})){app.quit();return;}
  const preferences=new Preferences(path.join(stateDir,'client-preferences.json'));
  const supervisor=new Supervisor(argumentsMap.get('--supervisor-pipe')!,root,rootHash,sessionId,Buffer.from(keyText,'hex'),build);
  let origin='',currentInstance='',expectedHandoff='',navigationGeneration=0,connection: ConnectionState='connecting';
  let candidate: {public: ConnectionCandidate;url: string;generation: number;frame: WebFrameMain;state: HostState}|null=null;
  let crashRetries=0;
  let lastReady: HostState|null=null;
  const manager=new WindowManager(stateDir,path.join(__dirname,'..','preload','index.js'),()=>{
    manager.window?.webContents.send('desktop.window-state-changed',manager.state());
    const state=manager.state();void supervisor.send('window.state',{state:state.minimized?'minimized':state.visible?'visible':'hidden',route:currentRoute()}).catch(()=>{});
  });
  manager.applyTheme(preferences.get().theme);
  const pageCommands=new PageCommands(()=>manager.window,()=>connection==='ready',
    (requestId,result)=>{void supervisor.send('page.command-result',{requestId,result}).catch(()=>{});});
  const applyNativeTheme=()=>manager.applyTheme(preferences.get().theme);
  nativeTheme.on('updated',applyNativeTheme);
  function currentRoute() {try {const hash=new URL(manager.window?.webContents.getURL()??'http://localhost').hash;return /^#\/[a-zA-Z0-9_/-]{0,256}$/.test(hash)?hash:'#/dashboard';}catch{return '#/dashboard';}}
  function state(value: ConnectionState) {connection=value;manager.window?.webContents.send('desktop.connection-state',value);}
  function invalidate(){candidate=null;}
  async function retryRenderer(window: Electron.BrowserWindow) {
    const host=lastReady;
    if(!host||!await verifyHttp(host,build)||manager.stopping||window.isDestroyed())return false;
    origin=`http://127.0.0.1:${host.actualPort}`;currentInstance=host.instanceId;
    await window.loadURL(origin+'/#/dashboard');state('ready');return true;
  }
  function authorized(event: IpcMainInvokeEvent|IpcMainEvent,bootstrap=false): boolean {
    const window=manager.window;
    if(!window||window.isDestroyed()||event.sender!==window.webContents||event.senderFrame!==window.webContents.mainFrame)return false;
    try {const url=new URL(event.senderFrame.url);return url.origin===origin&&origin!==''||bootstrap&&url.protocol==='nxp-desktop:'&&url.host==='bootstrap';}catch{return false;}
  }
  function businessWindow() {
    const existing=manager.window;const window=manager.create();if(existing===window)return window;
    pageCommands.attach(window);
    window.webContents.on('will-navigate',(event,url)=>{try{if(new URL(url).origin!==origin)event.preventDefault();}catch{event.preventDefault();}});
    window.webContents.on('will-redirect',(event,url)=>{try{if(new URL(url).origin!==origin)event.preventDefault();}catch{event.preventDefault();}});
    window.webContents.on('did-start-navigation',(_event,_url,_inPlace,isMainFrame)=>{if(isMainFrame){navigationGeneration++;invalidate();}});
    window.webContents.on('render-process-gone',async()=>{
      invalidate();state('disconnected');
      if(manager.stopping)return;
      await window.loadURL('nxp-desktop://bootstrap/index.html');
      if(crashRetries++===0&&await retryRenderer(window)) {
        await dialog.showMessageBox(window,{type:'warning',message:'界面已恢复，崩溃前未保存的输入已丢失。',buttons:['知道了']});
        return;
      }
      const answer=await dialog.showMessageBox(window,{type:'error',message:'界面恢复失败，未保存的输入已丢失。后台任务继续运行。',buttons:['重试连接','关闭客户端'],defaultId:1,cancelId:1});
      if(answer.response===0)await retryRenderer(window);
      else {manager.stop();supervisor.stop();app.quit();}
    });
    return window;
  }
  const handlers: Record<string,(arg?: unknown)=>unknown>={
    'desktop.window-state':()=>manager.state(),'desktop.minimize':()=>manager.window?.minimize(),
    'desktop.toggle-maximize':()=>{if(manager.window?.isMaximized())manager.window.unmaximize();else manager.window?.maximize();},
    'desktop.hide':()=>manager.window?.hide(),'desktop.preferences':()=>preferences.get(),
    'desktop.set-preferences':patch=>{preferences.set(patch as never);applyNativeTheme();},
    'desktop.write-clipboard-text':text=>{
      if(typeof text!=='string'||Buffer.byteLength(text,'utf8')>65536)throw new TypeError('Invalid clipboard text');
      clipboard.writeText(text);
    },
    'desktop.confirm-navigation':async id=>{
      if(typeof id!=='string'||! /^[0-9a-f-]{36}$/.test(id)||!candidate||candidate.public.candidateId!==id||candidate.generation!==navigationGeneration||candidate.frame!==manager.window?.webContents.mainFrame)return 'stale';
      const frozen=candidate;if(!await verifyHttp(frozen.state,build))return 'unavailable';
      if(candidate!==frozen)return 'stale';
      invalidate();origin=new URL(frozen.url).origin;currentInstance=frozen.state.instanceId;expectedHandoff='';
      const window=manager.window!;
      if(window.webContents.getURL()===frozen.url) {
        await new Promise<void>((resolve,reject)=>{
          const contents=window.webContents;
          const finish=()=>{cleanup();resolve();};
          const failed=()=>{cleanup();reject(new Error('Accepted Host navigation failed'));};
          const timer=setTimeout(failed,10000);
          const cleanup=()=>{clearTimeout(timer);contents.removeListener('did-finish-load',finish);contents.removeListener('did-fail-load',failed);};
          contents.once('did-finish-load',finish);contents.once('did-fail-load',failed);
          contents.reload();
        });
      } else await window.loadURL(frozen.url);
      state('ready');return 'accepted';
    },
  };
  for(const [channel,handler] of Object.entries(handlers))ipcMain.handle(channel,(event,arg)=>{
    if(!authorized(event,channel==='desktop.window-state'||channel==='desktop.hide'||channel==='desktop.preferences'))throw new Error('Desktop IPC sender rejected');return handler(arg);
  });
  ipcMain.on('desktop.subscribe',(event,channel)=>{
    if(!authorized(event,true))return;
    if(channel==='desktop.connection-candidate'&&candidate&&authorized(event))event.sender.send(channel,candidate.public);
    else if(channel==='desktop.connection-state')event.sender.send(channel,connection);
    else if(channel==='desktop.window-state-changed')event.sender.send(channel,manager.state());
  });
  ipcMain.on('desktop.page-command-result',(event,value)=>{if(authorized(event))pageCommands.result(event,value);});
  supervisor.on('page-command',command=>pageCommands.request(command));
  supervisor.on('state',async (host: HostState)=>{
    if(host.state!=='ready'){state(host.state==='restarting'?'restarting':'connecting');return;}
    if(!await verifyHttp(host,build)){state('disconnected');return;}
    lastReady=Object.freeze({...host});
    const window=businessWindow(),url=`http://127.0.0.1:${host.actualPort}/${currentRoute()}`;
    const loaded=window.webContents.getURL();
    if(!currentInstance||loaded.startsWith('nxp-desktop:')||!loaded){origin=new URL(url).origin;currentInstance=host.instanceId;await window.loadURL(url);state('ready');if(intent==='show')await manager.show();return;}
    if(currentInstance===host.instanceId){expectedHandoff='';state('ready');return;}
    const planned=expectedHandoff!=='';
    if(planned?host.handoffId!==expectedHandoff:host.previousInstanceId!==currentInstance){state('disconnected');return;}
    const value: ConnectionCandidate=Object.freeze({candidateId:crypto.randomUUID(),reason:planned?'host-restart':'host-recovery',instanceId:host.instanceId,frontendBuildId:build.frontendHash,desktopBuildId:build.buildId});
    candidate=Object.freeze({public:value,url,generation:navigationGeneration,frame:window.webContents.mainFrame,state:Object.freeze({...host})});
    state('restarting');window.webContents.send('desktop.connection-candidate',value);
  });
  supervisor.on('restart',(handoff: unknown)=>{if(typeof handoff==='string'&&/^[0-9a-f]{32}$/.test(handoff)){expectedHandoff=handoff;invalidate();state('restarting');}});
  supervisor.on('show',()=>{businessWindow();void manager.show().then(result=>supervisor.send('window.show-result',{result})).catch(()=>{});});
  supervisor.on('disconnected',()=>{pageCommands.invalidate();invalidate();state('disconnected');});
  supervisor.on('stop',async notice=>{
    if(notice.reason==='lightweight'&&!pageCommands.canStop(notice.transactionId))return;
    invalidate();state('stopping');
    if(notice.reason==='update'&&typeof notice.transactionId==='string'&&Number.isFinite(notice.remainingMs)) {
      manager.window?.webContents.send('desktop.shutdown-notice',{reason:'asset-update',transactionId:notice.transactionId,remainingMs:Math.max(0,notice.remainingMs),unsavedInputsWillBeSaved:false});
      await new Promise(resolve=>setTimeout(resolve,Math.min(300,Math.max(0,notice.remainingMs))));
    }
    manager.stop();await supervisor.send('desktop.stop-ready').catch(()=>{});supervisor.stop();app.quit();
  });
  app.on('second-instance',(_event,_argv,_cwd,data)=>{if(data&&typeof data==='object'&&'rootHash' in data&&'sessionId' in data&&data.rootHash===rootHash&&data.sessionId===sessionId)void supervisor.send('window.show-request').catch(()=>{});});
  app.on('before-quit',()=>{nativeTheme.removeListener('updated',applyNativeTheme);manager.stop();supervisor.stop();});
  app.on('window-all-closed',()=>{if(manager.stopping)app.quit();});
  await app.whenReady();
  protocol.handle('nxp-desktop',request=>{
    const url=new URL(request.url);if(url.host!=='bootstrap'||url.pathname!=='/index.html')return new Response('',{status:404});
    return new Response(fs.readFileSync(path.join(app.getAppPath(),'bootstrap','index.html')),{headers:{'Content-Type':'text/html; charset=utf-8','Content-Security-Policy':"default-src 'none'; style-src 'unsafe-inline'; script-src 'none'"}});
  });
  Menu.setApplicationMenu(Menu.buildFromTemplate([{label:'NexusPipeline',submenu:[{label:'隐藏窗口',click:()=>manager.window?.hide()},{label:'退出 NexusPipeline',click:()=>{void supervisor.send('host.exit-request').catch(()=>{});}},{label:'关闭此客户端',click:async()=>{
    const choice=await dialog.showMessageBox({type:'question',message:'关闭此客户端会丢失未保存的输入。后台任务继续运行。',buttons:['关闭客户端','取消'],defaultId:1,cancelId:1});
    if(choice.response===0){manager.stop();supervisor.stop();app.quit();}
  }}]}]));
  screen.on('display-added',()=>void manager.reevaluate());screen.on('display-removed',()=>void manager.reevaluate());screen.on('display-metrics-changed',()=>void manager.reevaluate());
  if(intent==='show'){businessWindow();await manager.window!.loadURL('nxp-desktop://bootstrap/index.html');await manager.show();}
  void supervisor.run();
}
void start().catch(async()=>{await app.whenReady();await dialog.showMessageBox({type:'error',title:'NexusPipeline',message:'桌面客户端无法建立受信任连接，请从根目录 NexusPipeline.exe 启动。',buttons:['关闭']});app.quit();});
