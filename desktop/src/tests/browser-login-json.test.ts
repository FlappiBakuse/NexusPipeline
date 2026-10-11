import assert from 'node:assert/strict';
import test from 'node:test';
import {captureJsonValue} from '../main/browser-login-json';

const rule={id:'passport',origin:'https://login.example.com',uri:'https://api.example.com/token',method:'GET' as const,jsonPath:['data','content']};
const signal=new AbortController().signal;
const json=(text:string)=>new Response(text,{headers:{'Content-Type':'application/json; charset=utf-8'}});

test('Fixed JSON capture returns only the selected value and binds session request policy',async()=>{
  const value=await captureJsonValue(rule,rule.origin,async(input,init)=>{
    assert.equal(input,rule.uri);assert.equal(init?.credentials,'include');assert.equal(init?.redirect,'error');
    assert.equal(init?.cache,'no-store');assert.equal(init?.signal,signal);assert.equal(init?.body,undefined);
    return json('{"data":{"content":"synthetic-token","other":"unselected"}}');
  },signal);
  assert.equal(value,'synthetic-token');
  assert.equal(await captureJsonValue(rule,rule.origin,async()=>json('{"code":401}'),signal),null);
});

test('Fixed JSON capture rejects origins, redirects, malformed or oversized responses and aborts',async()=>{
  let calls=0;
  const fetcher=async()=>{calls++;return json('{}');};
  await assert.rejects(captureJsonValue(rule,'https://other.example.com',fetcher,signal));assert.equal(calls,0);
  for(const uri of ['http://api.example.com/token','https://api.example.com/token?secret=x','https://user:pass@api.example.com/token'])
    await assert.rejects(captureJsonValue({...rule,uri},rule.origin,fetcher,signal));
  for(const response of [new Response('',{status:302,headers:{Location:'https://other.example.com'}}),new Response('{}'),json('{'),json('{"data":{"content":42}}'),json(' '.repeat(65537)),json(JSON.stringify({data:{content:'x'.repeat(16385)}}))])
    await assert.rejects(captureJsonValue(rule,rule.origin,async()=>response,signal));
  const abort=new AbortController();abort.abort();
  await assert.rejects(captureJsonValue(rule,rule.origin,async(_input,init)=>{init!.signal!.throwIfAborted();return json('{}');},abort.signal));
});
