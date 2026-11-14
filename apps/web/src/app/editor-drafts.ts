import type { DraftSettlement, EditorDraftScope } from '@nexuspipeline/extension-sdk';

class DraftScope implements EditorDraftScope {
  private readonly controller = new AbortController();
  private readonly values = new Map<string, object>();
  private readonly protectedValues = new Map<string, { value: object; clear: () => void }>();
  private readonly children = new Map<string, DraftScope>();
  private readonly listeners = new Set<(reason: DraftSettlement) => void>();
  private readonly guards = new Set<() => boolean>();
  private readonly saved = new Set<() => void>();
  private readonly changes = new Set<() => void>();
  private outcome?: DraftSettlement;
  readonly signal = this.controller.signal;

  constructor(private readonly report: (error: unknown) => void) {}

  retain<T extends object>(key: string, create: () => T): T {
    this.signal.throwIfAborted();
    if (!this.values.has(key)) this.values.set(key, create());
    return this.values.get(key) as T;
  }

  protect<T extends object>(key: string, create: () => T, clear: (value: T) => void): T {
    this.signal.throwIfAborted();
    if (!this.protectedValues.has(key)) {
      const value = create();
      Object.defineProperty(value, 'toJSON', { value: () => { throw new Error('secret_draft_requires_memory_port'); } });
      this.protectedValues.set(key, { value, clear: () => clear(value) });
    }
    return this.protectedValues.get(key)!.value as T;
  }

  child(key: string): DraftScope {
    this.signal.throwIfAborted();
    let child = this.children.get(key);
    if (!child || child.signal.aborted) {
      child = new DraftScope(this.report);
      this.children.set(key, child);
    }
    return child;
  }
  disposeChild(key: string): void {
    this.children.get(key)?.settle('disposed');
    this.children.delete(key);
  }

  onSettled(listener: (reason: DraftSettlement) => void): () => void {
    if (this.outcome) { listener(this.outcome); return () => {}; }
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }
  canCancel(): boolean {
    return [...this.guards].every(ready => ready()) && [...this.children.values()].every(child => child.canCancel());
  }
  guardCancel(ready: () => boolean): () => void {
    this.signal.throwIfAborted();
    this.guards.add(ready);
    return () => { this.guards.delete(ready); };
  }
  onParentSaved(listener: () => void): () => void {
    this.signal.throwIfAborted();
    this.saved.add(listener);
    return () => { this.saved.delete(listener); };
  }
  onChanged(listener: () => void): () => void {
    this.signal.throwIfAborted();
    this.changes.add(listener);
    return () => { this.changes.delete(listener); };
  }
  changed(): void {
    if (this.signal.aborted) return;
    for (const listener of this.changes) {
      try { listener(); } catch (error) { this.report(error); }
    }
  }
  parentSaved(): void {
    for (const listener of this.saved) {
      try { listener(); } catch (error) { this.report(error); }
    }
    for (const child of this.children.values()) child.parentSaved();
  }

  settle(reason: DraftSettlement): void {
    if (this.outcome) return;
    if (reason === 'cancel' && !this.canCancel()) throw new Error('editor_operation_pending');
    this.outcome = reason;
    this.controller.abort();
    for (const child of this.children.values()) child.settle(reason);
    for (const listener of this.listeners) {
      try { listener(reason); } catch (error) { this.report(error); }
    }
    for (const entry of this.protectedValues.values()) {
      try { entry.clear(); } catch (error) { this.report(error); }
    }
    this.children.clear();
    this.listeners.clear();
    this.guards.clear();
    this.saved.clear();
    this.changes.clear();
    this.protectedValues.clear();
    this.values.clear();
  }
}

export class EditorDraftStore {
  private readonly controller = new AbortController();
  readonly signal = this.controller.signal;
  private readonly targets = new Map<string, DraftScope>();
  constructor(private readonly report: (error: unknown) => void) {}

  target(instance: string, kind: string, id = ''): EditorDraftScope {
    this.signal.throwIfAborted();
    const key = JSON.stringify([instance, kind, id]);
    let scope = this.targets.get(key);
    if (!scope || scope.signal.aborted) {
      scope = new DraftScope(this.report);
      this.targets.set(key, scope);
    }
    return scope;
  }

  settle(instance: string, kind: string, id: string, reason: DraftSettlement): void {
    const key = JSON.stringify([instance, kind, id]);
    this.targets.get(key)?.settle(reason);
    this.targets.delete(key);
  }
  saved(instance: string, kind: string, id: string): void {
    this.targets.get(JSON.stringify([instance, kind, id]))?.parentSaved();
  }
  retireExtension(id: string): void {
    for (const scope of this.targets.values()) scope.disposeChild('extension/' + id);
  }

  dispose(): void {
    this.controller.abort();
    for (const scope of this.targets.values()) scope.settle('disposed');
    this.targets.clear();
  }
}
