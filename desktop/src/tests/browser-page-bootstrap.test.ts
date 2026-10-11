import assert from 'node:assert/strict';
import test from 'node:test';
import {BrowserPageBootstrap} from '../main/browser-page-bootstrap';

const rule={origin:'https://example.com',path:'/login',script:"()=> 'clicked'"};
function fixture(results:unknown[]=['clicked']) {
  let time=0,calls=0,url=rule.origin+rule.path;
  const identity={};
  const bootstrap=new BrowserPageBootstrap(rule,()=>({identity,url,run:async()=>{calls++;return results.shift()??'waiting';}}),
    {now:()=>time,pause:async()=>{time+=250;}});
  return {bootstrap,calls:()=>calls,url:(value:string)=>{url=value;}};
}

test('Page bootstrap waits for asynchronous entry and clicks once per full load',async()=>{
  const f=fixture(['waiting','waiting','clicked','clicked']);
  await f.bootstrap.loaded();assert.equal(f.calls(),3);
  await f.bootstrap.loaded();assert.equal(f.calls(),3);
  f.bootstrap.navigation();await f.bootstrap.loaded();assert.equal(f.calls(),4);
});

test('Page bootstrap stops for signed in, existing dialog, missing entry and disabled flows',async()=>{
  for(const result of ['already-authenticated','already-open']){
    const f=fixture([result]);await f.bootstrap.loaded();assert.equal(f.calls(),1);
  }
  const missing=fixture([]);await missing.bootstrap.loaded();assert.equal(missing.calls(),120);
  const disabled=new BrowserPageBootstrap(null,()=>{throw new Error('Disabled flow evaluated');});await disabled.loaded();
});

test('Navigation and cancellation invalidate pending page checks and reject other addresses',async()=>{
  for(const url of ['https://other.com/login','https://example.com/other','http://example.com/login']){
    const f=fixture();f.url(url);await f.bootstrap.loaded();assert.equal(f.calls(),0);
  }
  for(const navigate of ['cancel','full','other-path','same-path']){
    let release!:(value:unknown)=>void,calls=0;
    const bootstrap=new BrowserPageBootstrap(rule,()=>({identity:'main',url:rule.origin+rule.path,run:()=>{calls++;return calls===1?new Promise(resolve=>{release=resolve;}):Promise.resolve('clicked');}}),{now:()=>0,pause:async()=>{}});
    const pending=bootstrap.loaded();
    if(navigate==='full')bootstrap.navigation();
    else if(navigate==='cancel')bootstrap.stop();
    else bootstrap.inPageNavigation(rule.origin+(navigate==='same-path'?rule.path+'?initialized=1':'/other'));
    release('waiting');await pending;
    assert.equal(calls,navigate==='same-path'?2:1);
    if(navigate==='same-path'){await bootstrap.loaded();assert.equal(calls,2);}
  }
});
