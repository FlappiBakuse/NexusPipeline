export type ConnectionState = "connecting" | "ready" | "restarting" | "disconnected" | "stopping";
export interface PublicWindowState { readonly visible: boolean; readonly minimized: boolean; readonly maximized: boolean; }
export interface ClientPreferences { readonly locale: "zh-CN" | "en-US"; readonly theme: "light" | "dark" | "system"; }
export interface ConnectionCandidate {
  readonly candidateId: string;
  readonly reason: "host-restart" | "host-recovery";
  readonly instanceId: string;
  readonly frontendBuildId: string;
  readonly desktopBuildId: string;
}
export interface ShutdownNotice { readonly reason: "asset-update"; readonly transactionId: string; readonly remainingMs: number; readonly unsavedInputsWillBeSaved: false; }
export interface PageCommand {
  readonly kind: "reload" | "close-prepare" | "close-consume" | "close-release";
  readonly requestId: string;
  readonly leaseId: string;
  readonly expiresAt: number;
}
export type PageCommandResult = "ready" | "closing" | "released" | "reloading" | "no-page" | "busy" | "expired" | "disconnected" | "stale";
export interface NexusDesktopBridge {
  readonly bootstrapProtocolVersion: 1;
  getWindowState(): Promise<PublicWindowState>;
  minimize(): Promise<void>;
  toggleMaximize(): Promise<void>;
  hideWindow(): Promise<void>;
  getClientPreferences(): Promise<ClientPreferences>;
  setClientPreferences(patch: Partial<ClientPreferences>): Promise<void>;
  writeClipboardText(text: string): Promise<void>;
  openExternal(url: string): Promise<void>;
  getClientSessionToken(): Promise<string>;
  openBrowserLogin(request: BrowserLoginRequest): Promise<{operationId: string}>;
  onWindowStateChanged(listener: (state: PublicWindowState) => void): () => void;
  onConnectionStateChanged(listener: (state: ConnectionState) => void): () => void;
  onConnectionCandidate(listener: (candidate: ConnectionCandidate) => void): () => void;
  confirmConnectionNavigation(candidateId: string): Promise<"accepted" | "stale" | "unavailable">;
  onShutdownNotice(listener: (notice: ShutdownNotice) => void): () => void;
  onPageCommand(listener: (command: PageCommand) => void): () => void;
  reportPageCommand(requestId: string, result: PageCommandResult): void;
}
export interface BrowserLoginRequest {flowId:string;editorSessionId:string;fieldGeneration:number;context:Record<string,unknown>}
export interface BrowserLoginFlow {
  id:string;title:string;flowVersion:string;startUri:string;navigationOrigins:string[];popupOrigins:string[];
  cookies:{domain:string;path:string;name:string}[];
  storage:{origin:string;storage:'localStorage'|'sessionStorage';key:string;jsonPath:string[]}[];ready:boolean;
}
export interface BrowserLoginDescriptor {operationId:string;flow:BrowserLoginFlow;expiresAt:string}
