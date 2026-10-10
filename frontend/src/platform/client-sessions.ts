import {api,readAuthToken} from './api';
import {desktopBridge} from './desktop';
import {onServiceTrafficChanged} from './service-traffic';

export interface ClientSession {hostSessionId:string;clientSessionId:string;clientKind:'web'|'desktop';nativeBrowserAvailable:boolean}
let token='',authority:string|null=null,pending:Promise<ClientSession>|null=null,generation=0;
export function clientSessionHeaders():Record<string,string> {
  return token&&authority===readAuthToken()?{'X-Nxp-Client-Session':token}:{};
}
export function invalidateClientSession(){token='';pending=null;generation++;try{sessionStorage.removeItem('nxp-client-session');}catch{}}
onServiceTrafficChanged(paused=>{if(paused)invalidateClientSession();});
export async function getClientSession():Promise<ClientSession> {
  const auth=readAuthToken();
  if(authority!==auth){invalidateClientSession();authority=auth;}
  if(pending)return pending;
  const epoch=generation;
  const assertCurrent=()=>{if(epoch!==generation||auth!==readAuthToken())throw new Error('client_session_changed');};
  const work=async()=>{
    const bridge=desktopBridge();
    if(typeof bridge?.getClientSessionToken==='function'){
      const native=await bridge.getClientSessionToken();assertCurrent();token=native;
    }
    if(token)try{
      const session=await api<ClientSession>('GET','/api/client-sessions/current');assertCurrent();return session;
    }catch(error){assertCurrent();if((error as {code?:string}).code!=='client_session_required')throw error;token='';}
    // Resolving HTTP authentication can revoke the desktop token issued immediately before rotation.
    if(typeof bridge?.getClientSessionToken==='function') {
      const native=await bridge.getClientSessionToken();assertCurrent();token=native;
      const session=await api<ClientSession>('GET','/api/client-sessions/current');assertCurrent();return session;
    }
    // A duplicated web tab must not inherit another tab's disclosure permission.
    const created=await api<{token:string;session:ClientSession}>('POST','/api/client-sessions',{});
    assertCurrent();token=created.token;return created.session;
  };
  const current=work();pending=current;
  try{return await current;}finally{if(pending===current)pending=null;}
}
