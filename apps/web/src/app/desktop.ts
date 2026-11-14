export type ExitReadiness = 'ready' | 'pending-writes';
export interface ExitState { stage: 'preparing' | 'authorizing' | 'idle'; code?: string }

export interface DesktopChannel {
  version: number;
  presented?(): Promise<void>;
  preparation?(value: { stage: string; done: number; total: number }): Promise<void>;
  connect(): Promise<{ instanceId: string; route: string }>;
  setLocale(locale: 'zh-CN' | 'en-US'): Promise<void>;
  bindFrontend(clientId: string, token: string): Promise<unknown>;
  retireFrontend(clientId: string): Promise<void>;
  prepareExit(callback: (reason: 'close' | 'maintenance') => Promise<ExitReadiness>): void;
  onExitState(callback: (state: ExitState) => void): () => void;
  chooseFile(): Promise<{ canceled: boolean; paths: string[] }>;
  chooseFolder(): Promise<{ canceled: boolean; paths: string[] }>;
  openExternal(url: string): Promise<void>;
  hide(): Promise<void>;
  show(): Promise<void>;
  quit(): Promise<void>;
  minimize(): Promise<void>;
  toggleMaximize(): Promise<void>;
  close(): Promise<void>;
  windowState(): Promise<{ maximized: boolean }>;
  onWindowState(callback: (state: { maximized: boolean }) => void): () => void;
}
declare global {
  interface Window {
    nexusDesktopChannel?: DesktopChannel;
  }
}
export const desktop = window.nexusDesktopChannel;
