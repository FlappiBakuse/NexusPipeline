import type { Cleanup } from './extension-scope.js';

export type DraftSettlement = 'cancel' | 'saved' | 'disposed';

/** Memory belongs to the editor target, independently of a mounted surface. */
export interface EditorDraftScope {
  readonly signal: AbortSignal;
  retain<T extends object>(key: string, create: () => T): T;
  protect<T extends object>(key: string, create: () => T, clear: (value: T) => void): T;
  child(key: string): EditorDraftScope;
  canCancel(): boolean;
  guardCancel(ready: () => boolean): Cleanup;
  settle(reason: DraftSettlement): void;
  onSettled(listener: (reason: DraftSettlement) => void): Cleanup;
  onParentSaved(listener: () => void): Cleanup;
  onChanged(listener: () => void): Cleanup;
  changed(): void;
}
