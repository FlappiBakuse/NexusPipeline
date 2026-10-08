import {ref} from 'vue';
import {desktopBridge} from './desktop';
import {state} from './page-state';
import {pageWritesBusy,preparePageClose,consumePageClose,releasePageClose} from './page-writes';
import {toast} from './toast';
import {t} from './i18n';
import type {PageCommand} from '../../../desktop/src/shared/contracts';

export const browserPageRefresh=ref<{requestId: string;expiresAt: number;generation: number}|null>(null);
let expiration: ReturnType<typeof setTimeout>|null=null;
const seen=new Map<string,number>();

export function dismissPageRefresh(): void {
  if(expiration)clearTimeout(expiration);
  expiration=null;browserPageRefresh.value=null;
}
function busy(): void {toast(t('shell.page_refresh.busy'),'error');}

export function receiveBrowserPageRefresh(value: Record<string,unknown>|undefined): void {
  if(desktopBridge()||!value||Object.keys(value).sort().join(',')!=='expiresAt,requestId'
    ||typeof value.requestId!=='string'||!/^[0-9a-f]{32}$/.test(value.requestId)
    ||typeof value.expiresAt!=='number'||!Number.isSafeInteger(value.expiresAt)||value.expiresAt<=Date.now()||value.expiresAt>Date.now()+11000)return;
  for(const [id,expiresAt] of seen)if(expiresAt<=Date.now())seen.delete(id);
  if(seen.has(value.requestId))return;
  if(seen.size>=64)seen.delete(seen.keys().next().value!);
  seen.set(value.requestId,value.expiresAt);
  if(pageWritesBusy()){busy();return;}
  dismissPageRefresh();
  browserPageRefresh.value={requestId:value.requestId,expiresAt:value.expiresAt,generation:state.routeToken};
  expiration=setTimeout(dismissPageRefresh,value.expiresAt-Date.now());
}

export function confirmBrowserPageRefresh(): void {
  const request=browserPageRefresh.value;
  dismissPageRefresh();
  if(!request||request.expiresAt<=Date.now()||request.generation!==state.routeToken)return;
  if(pageWritesBusy()){busy();return;}
  location.reload();
}

function receiveDesktopCommand(command: PageCommand): void {
  const bridge=desktopBridge();if(!bridge)return;
  if(command.kind==='close-release'){releasePageClose(command.leaseId);bridge.reportPageCommand(command.requestId,'released');return;}
  if(command.expiresAt<=Date.now()){bridge.reportPageCommand(command.requestId,'expired');return;}
  if(pageWritesBusy()){bridge.reportPageCommand(command.requestId,'busy');return;}
  if(command.kind==='close-prepare') {
    bridge.reportPageCommand(command.requestId,preparePageClose(command.leaseId,command.expiresAt)?'ready':'busy');return;
  }
  if(command.kind==='close-consume') {
    bridge.reportPageCommand(command.requestId,consumePageClose(command.leaseId)?'closing':'stale');return;
  }
  if(command.kind==='reload') {
    // The final write check and navigation share one synchronous renderer turn.
    bridge.reportPageCommand(command.requestId,'reloading');
    location.reload();
  }
}

export function startPageRefresh(): ()=>void {
  const dispose=desktopBridge()?.onPageCommand(receiveDesktopCommand);
  return ()=>{dispose?.();dismissPageRefresh();seen.clear();releasePageClose();};
}
