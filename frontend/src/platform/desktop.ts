import type {NexusDesktopBridge,ClientPreferences} from '../../../desktop/src/shared/contracts';
export type {ConnectionCandidate,ConnectionState,PublicWindowState} from '../../../desktop/src/shared/contracts';
export function desktopBridge(): NexusDesktopBridge|null {
  const bridge=(window as Window&{nexusDesktop?: NexusDesktopBridge}).nexusDesktop;
  return bridge?.bootstrapProtocolVersion===1&&typeof bridge.confirmConnectionNavigation==='function'?bridge:null;
}
export async function initializeDesktopPreferences(): Promise<ClientPreferences|null> {
  const bridge=desktopBridge();if(!bridge)return null;
  document.documentElement.dataset.desktop='true';
  let preferences: ClientPreferences;
  try {preferences=await bridge.getClientPreferences();} catch {document.documentElement.dataset.desktopPreferences='unavailable';return null;}
  if(!['zh-CN','en-US'].includes(preferences.locale)||!['light','dark','system'].includes(preferences.theme))return null;
  try{localStorage.setItem('nexus-locale',preferences.locale);localStorage.setItem('nexus-theme',preferences.theme);}catch{}
  return preferences;
}
