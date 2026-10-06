import {BrowserWindow,dialog,screen,nativeTheme} from "electron";
import fs from "node:fs";
import path from "node:path";
import {atomicJson} from "./client-preferences";
import {chooseWindowPlacement,validSaved,SavedPlacement,DisplaySnapshot} from "./window-placement";
export class WindowManager {
  window: BrowserWindow|null=null;
  stopping=false;
  private saved: SavedPlacement|null=null;
  private timer: NodeJS.Timeout|null=null;
  private theme: 'system'|'light'|'dark'='system';
  constructor(private readonly stateDir: string,private readonly preload: string,private readonly onState: ()=>void) {
    const file=path.join(stateDir,'window-state.json');
    try {if(!fs.lstatSync(file).isSymbolicLink()&&fs.statSync(file).size<=4096){const value=JSON.parse(fs.readFileSync(file,'utf8'));if(validSaved(value))this.saved=value;}} catch { }
  }
  state() {return Object.freeze({visible:this.window?.isVisible()??false,minimized:this.window?.isMinimized()??false,maximized:this.window?.isMaximized()??false});}
  create(): BrowserWindow {
    if(this.window&&!this.window.isDestroyed()) return this.window;
    const window=new BrowserWindow({width:1280,height:760,minWidth:1280,minHeight:760,useContentSize:true,show:false,
      title:'NexusPipeline',icon:path.join(process.resourcesPath,'NexusPipeline.ico'),titleBarStyle:'hidden',titleBarOverlay:{height:40,color:'#ffffff',symbolColor:'#37352f'},
      webPreferences:{preload:this.preload,sandbox:true,contextIsolation:true,nodeIntegration:false,webSecurity:true,spellcheck:false}});
    this.window=window;
    this.applyTheme(this.theme);
    window.on('close',event=>{if(!this.stopping){event.preventDefault();this.flush();window.hide();this.onState();}});
    window.on('closed',()=>{this.window=null;this.onState();});
    window.on('show',this.onState);window.on('hide',this.onState);window.on('minimize',this.onState);window.on('restore',this.onState);window.on('maximize',this.onState);window.on('unmaximize',this.onState);
    const remember=()=>{if(this.timer)clearTimeout(this.timer);this.timer=setTimeout(()=>this.flush(),200);};
    window.on('move',remember);window.on('resize',remember);
    window.webContents.setWindowOpenHandler(()=>({action:'deny'}));
    window.webContents.session.setPermissionRequestHandler((_contents,_permission,callback)=>callback(false));
    window.webContents.session.setPermissionCheckHandler(()=>false);
    window.webContents.on('will-attach-webview',event=>event.preventDefault());
    window.webContents.setZoomFactor(1);
    return window;
  }
  async show(): Promise<'shown'|'kept-background'|'open-cancelled'> {
    const window=this.create();
    const displays=screen.getAllDisplays() as DisplaySnapshot[];
    const size=window.getSize(),content=window.getContentSize();
    const placement=chooseWindowPlacement({displays,primaryDisplayId:screen.getPrimaryDisplay().id,saved:this.saved,launchIntent:'show',titleBarHeightDip:40,frameInsetsDip:{width:Math.max(0,size[0]-content[0]),height:Math.max(0,size[1]-content[1])}});
    if(placement.kind==='background') return 'kept-background';
    if(placement.kind==='needs-low-resolution-choice') {
      const answer=await dialog.showMessageBox({type:'warning',title:'NexusPipeline',message:'当前显示工作区不足以容纳 1280 × 720 的业务界面。',detail:'继续打开可能使部分窗口超出工作区。后台任务和托盘可继续运行。',buttons:['继续打开','仅后台运行','取消打开'],defaultId:1,cancelId:2,noLink:true});
      if(answer.response!==0) {window.hide();return answer.response===1?'kept-background':'open-cancelled';}
      const area=placement.availableWorkAreaDip;window.setContentSize(1280,760);window.setPosition(area.x,area.y);
    } else {
      window.setBounds(placement.outerBoundsDip);
      if(placement.maximized)window.maximize();
    }
    if(window.isMinimized())window.restore();window.show();window.focus();return 'shown';
  }
  async reevaluate() {if(this.window?.isVisible()&&!this.stopping) await this.show();}
  applyTheme(theme: 'system'|'light'|'dark') {
    this.theme=theme;
    const dark=theme==='dark'||theme==='system'&&nativeTheme.shouldUseDarkColors;
    this.window?.setTitleBarOverlay({height:40,color:dark?'#131f33':'#ffffff',symbolColor:dark?'#edf5ff':'#37352f'});
  }
  flush() {
    const window=this.window;if(!window||window.isDestroyed()||!window.isVisible()||window.isMinimized())return;
    const bounds=window.getNormalBounds(),size=window.getSize(),content=window.getContentSize();
    const display=screen.getDisplayMatching(bounds);
    const value: SavedPlacement={schemaVersion:1,x:bounds.x,y:bounds.y,businessWidth:bounds.width-Math.max(0,size[0]-content[0]),businessHeight:bounds.height-Math.max(0,size[1]-content[1])-40,displayId:display.id,scaleFactor:display.scaleFactor,maximized:window.isMaximized()};
    if(validSaved(value)){atomicJson(path.join(this.stateDir,'window-state.json'),value);this.saved=value;}
  }
  stop() {this.flush();this.stopping=true;if(this.timer)clearTimeout(this.timer);this.window?.close();}
}
