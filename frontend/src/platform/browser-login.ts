import {api} from './api';
import {desktopBridge} from './desktop';
import {getClientSession} from './client-sessions';
import type {BrowserLoginRequest} from '../../../desktop/src/shared/contracts';

export interface BrowserLoginResult {success:boolean;candidateId?:string;configured:boolean;source?:'browser';maskedAccount?:string;error?:string}
export interface BrowserLoginOperation {operationId:string;completed:Promise<BrowserLoginResult>;cancel:()=>Promise<void>}
export async function openBrowserLogin(request:BrowserLoginRequest,signal?:AbortSignal):Promise<BrowserLoginOperation> {
  const client=await getClientSession(),bridge=desktopBridge();signal?.throwIfAborted();
  if(!client.nativeBrowserAvailable||typeof bridge?.openBrowserLogin!=='function')throw new Error('client_not_supported');
  const {operationId}=await bridge.openBrowserLogin(request);
  let stopped=false;
  const cancel=async()=>{if(stopped)return;stopped=true;await api('DELETE',`/api/browser-login/${encodeURIComponent(operationId)}`);};
  const aborted=()=>{void cancel().catch(()=>{});};
  signal?.addEventListener('abort',aborted,{once:true});if(signal?.aborted)aborted();
  const completed=(async()=>{
    try {
      while(!stopped) {
        signal?.throwIfAborted();
        const value=await api<{state:string;result:BrowserLoginResult|null}>('GET',`/api/browser-login/${encodeURIComponent(operationId)}`,undefined,signal);
        if(['completed','cancelled','failed'].includes(value.state)){stopped=true;return value.result||{success:false,configured:false,error:'browser_operation_failed'};}
        await new Promise<void>(resolve=>setTimeout(resolve,500));
      }
      throw new DOMException('Cancelled','AbortError');
    } catch(error) {await cancel().catch(()=>{});throw error;}
    finally {signal?.removeEventListener('abort',aborted);}
  })();
  return {operationId,completed,cancel};
}
