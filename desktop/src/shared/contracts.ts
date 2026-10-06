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
export interface NexusDesktopBridge {
  readonly bootstrapProtocolVersion: 1;
  getWindowState(): Promise<PublicWindowState>;
  minimize(): Promise<void>;
  toggleMaximize(): Promise<void>;
  hideWindow(): Promise<void>;
  getClientPreferences(): Promise<ClientPreferences>;
  setClientPreferences(patch: Partial<ClientPreferences>): Promise<void>;
  writeClipboardText(text: string): Promise<void>;
  onWindowStateChanged(listener: (state: PublicWindowState) => void): () => void;
  onConnectionStateChanged(listener: (state: ConnectionState) => void): () => void;
  onConnectionCandidate(listener: (candidate: ConnectionCandidate) => void): () => void;
  confirmConnectionNavigation(candidateId: string): Promise<"accepted" | "stale" | "unavailable">;
  onShutdownNotice(listener: (notice: ShutdownNotice) => void): () => void;
}
