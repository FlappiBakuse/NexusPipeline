import crypto from "node:crypto";
import {EventEmitter} from "node:events";
import {WindowsPipe} from "./windows-pipe";
import {BuildRecord,HostState,fields} from "./host-identity";
export function proof(key: Buffer,direction: "host"|"client",root: string,session: string,clientNonce: string,hostNonce: string,instance: string,buildId: string): string {
  return crypto.createHmac('sha256',key).update([direction,'1',root,'g0170',session,clientNonce,hostNonce,instance,buildId].join('\n'),'utf8').digest('hex');
}
export class Supervisor extends EventEmitter {
  private pipe: WindowsPipe|null=null;
  private stopped=false;
  constructor(private readonly pipeName: string,private readonly root: string,private readonly rootHash: string,readonly sessionId: string,private readonly key: Buffer,private readonly build: BuildRecord) {super();}
  async run(): Promise<void> {
    let backoff=100;
    while(!this.stopped) {
      try {await this.connect();backoff=100;} catch {this.emit('disconnected');}
      this.pipe?.dispose();this.pipe=null;
      if(!this.stopped) {await new Promise<void>(resolve=>setTimeout(resolve,backoff));backoff=Math.min(5000,backoff*2);}
    }
    this.key.fill(0);
  }
  stop(): void {this.stopped=true;this.pipe?.dispose();}
  async send(type: string,data: Record<string,unknown>={}): Promise<void> {
    if(!this.pipe) throw new Error('desktop_pipe_disconnected');
    await this.pipe.send({type,requestId:crypto.randomUUID(),data});
  }
  private async connect() {
    const pipe=new WindowsPipe(this.pipeName,this.root);this.pipe=pipe;
    const clientNonce=crypto.randomBytes(32).toString('hex');
    await pipe.send({type:'hello',requestId:'attach',data:{role:'desktop',protocol:1,rootHash:this.rootHash,generation:'g0170',sessionId:this.sessionId,clientNonce}});
    const challenge=await pipe.receive();fields(challenge,['type','requestId','data']);fields(challenge.data,['hostNonce','instanceId','buildId','hostPid','hostStartFileTime','proof']);
    const data=challenge.data;
    if(challenge.type!=='challenge'||challenge.requestId!=='attach'||! /^[0-9a-f]{64}$/.test(String(data.hostNonce))||! /^[0-9a-f]{32}$/.test(String(data.instanceId))
      ||data.buildId!==this.build.buildId||data.hostPid!==pipe.peer.pid||data.hostStartFileTime!==pipe.peer.startFileTime) throw new Error('Invalid supervisor peer');
    const expected=proof(this.key,'host',this.rootHash,this.sessionId,clientNonce,String(data.hostNonce),String(data.instanceId),this.build.buildId);
    if(typeof data.proof!=='string'||! /^[0-9a-f]{64}$/.test(data.proof)||!crypto.timingSafeEqual(Buffer.from(data.proof,'hex'),Buffer.from(expected,'hex'))) throw new Error('Supervisor authentication failed');
    await pipe.send({type:'proof',requestId:'attach',data:{proof:proof(this.key,'client',this.rootHash,this.sessionId,clientNonce,String(data.hostNonce),String(data.instanceId),this.build.buildId)}});
    const attached=await pipe.receive();fields(attached,['type','requestId','data']);fields(attached.data,[]);
    if(attached.type!=='attached'||attached.requestId!=='attach') throw new Error('Supervisor attach rejected');
    while(!this.stopped) {
      const message=await pipe.receive(15000);fields(message,['type','requestId','data']);
      if(typeof message.requestId!=='string'||message.requestId.length>64) throw new Error('Invalid request ID');
      switch(message.type) {
        case 'host.state': {
          fields(message.data,['state','actualPort','instanceId','previousInstanceId','handoffId','desktopBuildId','frontendBuildId']);
          const state=message.data as unknown as HostState;
          if(!['starting','ready','restarting','stopping'].includes(state.state)||state.instanceId!==data.instanceId||state.desktopBuildId!==this.build.buildId||state.frontendBuildId!==this.build.frontendHash) throw new Error('Invalid Host state');
          this.emit('state',Object.freeze({...state}));break;
        }
        case 'window.show': fields(message.data,['reason']);this.emit('show');break;
        case 'host.restart-preparing': fields(message.data,['handoffId']);this.emit('restart',message.data.handoffId);break;
        case 'desktop.prepare-stop': fields(message.data,['reason','transactionId','remainingMs']);this.emit('stop',Object.freeze({...message.data}));break;
        case 'page.command': {
          fields(message.data,['kind','requestId','leaseId','expiresAt']);
          const value=message.data;
          if(!['reload','close-prepare','close-consume','close-release'].includes(String(value.kind))
            ||typeof value.requestId!=='string'||!/^[0-9a-f]{32}$/.test(value.requestId)
            ||typeof value.leaseId!=='string'||(value.kind==='reload'?value.leaseId!=='':!/^[0-9a-f]{32}$/.test(value.leaseId))
            ||typeof value.expiresAt!=='number'||!Number.isSafeInteger(value.expiresAt)||value.expiresAt>Date.now()+11000)throw new Error('Invalid page command');
          this.emit('page-command',Object.freeze({...value}));break;
        }
        case 'ping': fields(message.data,[]);await this.send('pong');break;
        default: throw new Error('Unknown supervisor message');
      }
    }
  }
}
