import type { Cleanup } from './extension-scope.js';
import type { SurfaceContext } from './frontend-extension.js';

export interface FrontendImage {
  readonly blob: Blob;
  readonly width: number;
  readonly height: number;
  readonly capturedAt?: string;
  readonly source?: 'pc_game' | 'emulator';
  release(): void;
}

export type FrontendPreview =
  | { readonly state: 'ready'; readonly image: FrontendImage; readonly capturedAt: string }
  | { readonly state: 'waiting_run' | 'waiting_for_game' | 'window_not_ready' | 'emulator_not_ready' | 'unavailable' };

export interface FrontendTransport {
  readonly connectionKind: 'local' | 'remote';
  invoke(operation: string, input: unknown, signal: AbortSignal, operationId?: string): Promise<unknown>;
  upload(operation: string, file: File, signal: AbortSignal, operationId: string): Promise<unknown>;
  image(operation: string, assetId: string, signal: AbortSignal): Promise<FrontendImage>;
  preview(context: SurfaceContext, providerId: string, signal: AbortSignal): Promise<FrontendPreview>;
  project(context: SurfaceContext, action: 'context' | 'discover' | 'select' | 'result' | 'preview' | 'page' | 'release',
    input: unknown, signal: AbortSignal, operationId?: string): Promise<unknown>;
  subscribe(listener: () => void): Cleanup;
}
