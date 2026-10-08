import type {BrowserWindow, IpcMainEvent, WebFrameMain} from 'electron';
import type {PageCommand, PageCommandResult} from '../shared/contracts';
import {fields} from './host-identity';

type Pending = {command: PageCommand; frame: WebFrameMain; url: string; generation: number; timer: ReturnType<typeof setTimeout>; started: boolean; loaded: boolean; acknowledged: boolean};

export class PageCommands {
  private generation=0;
  private readonly pending=new Map<string,Pending>();
  private lease: {id: string; expiresAt: number; frame: WebFrameMain; generation: number; consumed: boolean}|null=null;
  constructor(private readonly window: ()=>BrowserWindow|null, private readonly ready: ()=>boolean,
    private readonly reply: (requestId: string,result: PageCommandResult)=>void) {}

  attach(window: BrowserWindow): void {
    const contents=window.webContents;
    contents.on('did-start-navigation',(_event,url,inPlace,main)=>{
      if(!main)return;
      // Vue rehydrates the unchanged hash through same-document navigation before load finishes.
      if(inPlace&&[...this.pending.values()].some(pending=>pending.command.kind==='reload'&&pending.started&&pending.url===url)) {
        this.lease=null;return;
      }
      this.generation++;this.lease=null;
      for(const [id,pending] of this.pending) {
        if(pending.command.kind==='reload'&&!pending.started&&!inPlace&&pending.url===url&&this.generation===pending.generation+1)pending.started=true;
        else this.finish(id,'stale');
      }
    });
    contents.on('did-finish-load',()=>{
      for(const [id,pending] of this.pending)if(pending.started) {
        pending.loaded=true;
        if(pending.acknowledged)this.finish(id,'reloading');
      }
    });
    contents.on('did-fail-load',()=>this.invalidate());
    contents.on('render-process-gone',()=>this.invalidate());
    window.on('closed',()=>this.invalidate());
  }

  request(command: PageCommand): void {
    const window=this.window();
    if(command.expiresAt<=Date.now()){this.reply(command.requestId,'expired');return;}
    if(!window||window.isDestroyed()||!this.ready()||!/^http:\/\/127\.0\.0\.1:\d+\//.test(window.webContents.getURL())){this.reply(command.requestId,'no-page');return;}
    if(command.kind==='close-consume'&&!this.validLease(command.leaseId)){this.reply(command.requestId,'stale');return;}
    if(this.pending.size){this.reply(command.requestId,'busy');return;}
    const pending: Pending={command,frame:window.webContents.mainFrame,url:window.webContents.getURL(),generation:this.generation,
      timer:setTimeout(()=>this.finish(command.requestId,'expired'),Math.max(1,command.expiresAt-Date.now())),started:false,loaded:false,acknowledged:false};
    this.pending.set(command.requestId,pending);
    window.webContents.send('desktop.page-command',command);
  }

  result(event: IpcMainEvent,value: unknown): void {
    try {fields(value,['requestId','result']);}catch{return;}
    const response=value as {requestId?: unknown;result?: unknown};
    if(typeof response.requestId!=='string'||typeof response.result!=='string')return;
    const pending=this.pending.get(response.requestId),window=this.window();
    if(!pending||!window||event.sender!==window.webContents||event.senderFrame!==pending.frame)return;
    if(pending.command.expiresAt<=Date.now()){this.finish(response.requestId,'expired');return;}
    if(!['ready','closing','released','reloading','busy','expired','stale'].includes(response.result))return;
    const result=response.result as PageCommandResult;
    if(pending.command.kind==='reload'&&result==='reloading') {
      pending.acknowledged=true;
      if(pending.started&&pending.loaded)this.finish(response.requestId,'reloading');
      return;
    }
    if(pending.generation!==this.generation){this.finish(response.requestId,'stale');return;}
    if(pending.command.kind==='close-prepare'&&result==='ready')this.lease={id:pending.command.leaseId,expiresAt:pending.command.expiresAt,frame:pending.frame,generation:pending.generation,consumed:false};
    else if(pending.command.kind==='close-consume'&&result==='closing') {
      if(!this.validLease(pending.command.leaseId)){this.finish(response.requestId,'stale');return;}
      this.lease!.consumed=true;
    } else if(pending.command.kind==='close-release'&&result==='released')this.lease=null;
    else if(['ready','closing','released'].includes(result)){this.finish(response.requestId,'stale');return;}
    this.finish(response.requestId,result);
  }

  canStop(leaseId: string): boolean {return this.validLease(leaseId)&&this.lease!.consumed;}
  invalidate(): void {this.lease=null;for(const id of this.pending.keys())this.finish(id,'disconnected');}
  private validLease(id: string): boolean {return !!this.lease&&this.lease.id===id&&this.lease.expiresAt>Date.now()&&this.lease.generation===this.generation&&this.lease.frame===this.window()?.webContents.mainFrame;}
  private finish(id: string,result: PageCommandResult): void {
    const pending=this.pending.get(id);if(!pending)return;
    clearTimeout(pending.timer);this.pending.delete(id);this.reply(id,result);
  }
}
