import type {BrowserLoginFlow} from '../shared/contracts';

type Rule = NonNullable<BrowserLoginFlow['jsonRequests']>[number];

export async function captureJsonValue(rule:Rule, origin:string, fetcher:(url:string, init:RequestInit)=>Promise<Response>, signal:AbortSignal):Promise<string|null> {
  const uri=new URL(rule.uri);
  if(rule.origin!==origin||uri.protocol!=='https:'||uri.username||uri.password||uri.port||uri.hash||uri.search
    ||!['GET','POST'].includes(rule.method)||rule.jsonPath.length<1||rule.jsonPath.length>8)
    throw new Error('browser_json_rule_invalid');
  const response=await fetcher(rule.uri,{method:rule.method,credentials:'include',redirect:'error',cache:'no-store',signal,
    headers:{Accept:'application/json',Origin:origin,Referer:origin+'/'}});
  if(!response.ok||!/^application\/json(?:\s*;|$)/i.test(response.headers.get('content-type')||'')||!response.body)
    throw new Error('browser_json_response_invalid');
  const reader=response.body.getReader();
  const parts:Uint8Array[]=[];
  let size=0;
  try {
    while(true){const {done,value}=await reader.read();if(done)break;size+=value.byteLength;
      if(size>65536)throw new Error('browser_capture_limit');parts.push(value);}
  } finally {await reader.cancel().catch(()=>{});reader.releaseLock();}
  let value:unknown=JSON.parse(Buffer.concat(parts).toString('utf8'));
  for(const key of rule.jsonPath){
    if(['__proto__','constructor','prototype'].includes(key))throw new Error('browser_json_rule_invalid');
    if(value===null||typeof value!=='object'||!Object.prototype.hasOwnProperty.call(value,key))return null;
    value=(value as Record<string,unknown>)[key];
  }
  if(value!==null&&typeof value!=='string')throw new Error('browser_capture_invalid');
  if(typeof value==='string'&&Buffer.byteLength(value,'utf8')>16384)throw new Error('browser_capture_limit');
  return value;
}
