import {test} from 'node:test';
import assert from 'node:assert/strict';
import {EventEmitter} from 'node:events';
import type {BrowserWindow,IpcMainEvent} from 'electron';
import {PageCommands} from '../main/page-commands';
import type {PageCommandResult} from '../shared/contracts';

function fixture() {
  const contents=Object.assign(new EventEmitter(),{mainFrame:{},getURL:()=> 'http://127.0.0.1:58731/#/settings',send:()=>{}});
  const window=Object.assign(new EventEmitter(),{webContents:contents,isDestroyed:()=>false});
  const replies: Array<[string,PageCommandResult]>=[];
  const commands=new PageCommands(()=>window as unknown as BrowserWindow,()=>true,(id,result)=>replies.push([id,result]));
  commands.attach(window as unknown as BrowserWindow);
  const event={sender:contents,senderFrame:contents.mainFrame} as unknown as IpcMainEvent;
  return {commands,contents,replies,event};
}
test('reload requires both the current renderer acknowledgment and completed navigation',()=>{
  const {commands,contents,replies,event}=fixture();
  commands.request({kind:'reload',requestId:'reload',leaseId:'',expiresAt:Date.now()+1000});
  commands.result(event,{requestId:'reload',result:'reloading'});
  assert.deepEqual(replies,[]);
  contents.emit('did-start-navigation',{},'http://127.0.0.1:58731/#/settings',false,true);
  assert.deepEqual(replies,[]);
  contents.emit('did-finish-load');
  assert.deepEqual(replies,[['reload','reloading']]);
});
test('a close permit is invalidated by navigation and cannot authorize another frame',()=>{
  const {commands,contents,replies,event}=fixture();
  commands.request({kind:'close-prepare',requestId:'prepare',leaseId:'lease',expiresAt:Date.now()+1000});
  commands.result({...event,senderFrame:{}} as IpcMainEvent,{requestId:'prepare',result:'ready'});
  assert.deepEqual(replies,[]);
  commands.result(event,{requestId:'prepare',result:'ready'});
  commands.request({kind:'close-consume',requestId:'consume',leaseId:'lease',expiresAt:Date.now()+1000});
  commands.result(event,{requestId:'consume',result:'closing'});
  assert.equal(commands.canStop('lease'),true);
  contents.emit('did-start-navigation',{},'',true,true);
  assert.equal(commands.canStop('lease'),false);
  commands.invalidate();
});

test('reload accepts unchanged hash rehydration but still requires a completed document load',()=>{
  const {commands,contents,replies,event}=fixture();
  commands.request({kind:'reload',requestId:'reload',leaseId:'',expiresAt:Date.now()+1000});
  commands.result(event,{requestId:'reload',result:'reloading'});
  contents.emit('did-start-navigation',{},contents.getURL(),false,true);
  contents.emit('did-start-navigation',{},contents.getURL(),true,true);
  contents.emit('did-start-navigation',{},contents.getURL(),true,true);
  assert.deepEqual(replies,[]);
  contents.emit('did-finish-load');
  assert.deepEqual(replies,[['reload','reloading']]);
});

test('reload rejects a changed route and same-document navigation without a document reload',()=>{
  for(const started of [false,true]) {
    const {commands,contents,replies,event}=fixture();
    commands.request({kind:'reload',requestId:'reload',leaseId:'',expiresAt:Date.now()+1000});
    commands.result(event,{requestId:'reload',result:'reloading'});
    if(started)contents.emit('did-start-navigation',{},contents.getURL(),false,true);
    contents.emit('did-start-navigation',{},started?'http://127.0.0.1:58731/#/history':contents.getURL(),true,true);
    contents.emit('did-finish-load');
    assert.deepEqual(replies,[['reload','stale']]);
  }
});
