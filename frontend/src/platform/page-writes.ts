import {state} from './page-state';

let pending=0;
let permit: {id: string; expiresAt: number; generation: number}|null=null;

function currentPermit() {
  if(permit&&(permit.expiresAt<=Date.now()||permit.generation!==state.routeToken))permit=null;
  return permit;
}

export function pageWritesBusy(): boolean {return pending>0;}
export function beginPageWrite(): ()=>void {
  if(currentPermit())throw new Error('page_close_pending');
  pending++;
  let active=true;
  return ()=>{if(active){active=false;pending--;}};
}
export function preparePageClose(id: string,expiresAt: number): boolean {
  if(pageWritesBusy()||expiresAt<=Date.now()||currentPermit())return false;
  permit={id,expiresAt,generation:state.routeToken};
  return true;
}
export function consumePageClose(id: string): boolean {return !pageWritesBusy()&&currentPermit()?.id===id;}
export function releasePageClose(id?: string): void {if(!id||permit?.id===id)permit=null;}
