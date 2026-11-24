import { abortable } from './abortable';

type SharedRead = {
  work: Promise<unknown>;
  controller: AbortController;
  generation: number;
  directoryScope: string | undefined;
  preparationGeneration: number;
  prepared: boolean;
  users: number;
  settled: boolean;
};
type Decoder<T> = (value: unknown) => T;
export type EditorDirectory = 'extensions' | 'notifications';

export class PreparedReadRevoked extends Error {
  constructor() { super('prepared_read_revoked'); }
}

export class ControlReads {
  private readonly prepared = new Map<string, SharedRead>();
  private readonly shared = new Map<string, SharedRead>();
  private readonly directories = new Map<EditorDirectory, { scope: string; item: SharedRead }>();
  private generation = 0;
  private preparationGeneration = 0;

  constructor(private readonly request: (path: string, signal?: AbortSignal) => Promise<unknown>) {}

  clear(): void {
    this.generation++;
    this.invalidatePrepared();
    for (const item of this.shared.values()) item.controller.abort(new PreparedReadRevoked());
    this.shared.clear();
    this.invalidateDirectories();
  }
  invalidateDirectories(): void {
    for (const { item } of this.directories.values()) item.controller.abort(new PreparedReadRevoked());
    this.directories.clear();
  }
  invalidatePrepared(): void {
    this.preparationGeneration++;
    this.prepared.clear();
    for (const [path, item] of this.shared) if (item.prepared) {
      item.controller.abort(new PreparedReadRevoked());
      this.shared.delete(path);
    }
  }
  async prepare<T = unknown>(path: string, signal: AbortSignal, decode?: Decoder<T>, directoryScope?: string): Promise<T> {
    signal.throwIfAborted();
    const usable = (item: SharedRead | undefined): item is SharedRead => !!item && !item.controller.signal.aborted &&
      (directoryScope === undefined || item.directoryScope === directoryScope);
    let item = this.prepared.get(path);
    if (!usable(item)) {
      this.prepared.delete(path);
      if (this.prepared.size >= 32) throw new Error('prepared_read_capacity');
      item = this.shared.get(path);
      if (!usable(item)) {
        if (this.shared.size >= 32 && !this.shared.has(path)) throw new Error('prepared_read_capacity');
        item = this.startShared(path, true, directoryScope);
      }
      item.prepared = true;
      item.preparationGeneration = this.preparationGeneration;
      this.prepared.set(path, item);
    }
    try {
      const value = await this.joinShared(path, item, signal);
      return decode ? decode(value) : value as T;
    } catch (error) {
      if ((!signal.aborted || item.users === 0) && this.prepared.get(path) === item) this.prepared.delete(path);
      throw error;
    }
  }
  async read<T>(path: string, decode: Decoder<T>, signal?: AbortSignal): Promise<T> {
    signal?.throwIfAborted();
    let item = this.prepared.get(path);
    if (item) this.prepared.delete(path);
    item ||= this.shared.get(path);
    if (!item && this.shared.size < 32 && /^\/control\/v1\/records\/[^/]+\/attempts\/[^/]+\/report$/.test(path))
      item = this.startShared(path, false);
    if (item) {
      try { return decode(await this.joinShared(path, item, signal)); }
      catch (error) {
        if (!signal?.aborted && item.generation === this.generation && item.prepared &&
            item.preparationGeneration !== this.preparationGeneration) return this.read(path, decode, signal);
        throw error;
      }
    }
    return decode(await this.request(path, signal));
  }

  async readDirectory<T>(name: EditorDirectory, scope: string, reusable: boolean, decode: Decoder<T>, signal: AbortSignal): Promise<T> {
    signal.throwIfAborted();
    const path = '/control/v1/' + name;
    let current = this.directories.get(name);
    if (current && (current.scope !== scope || current.item.controller.signal.aborted || (!reusable && current.item.settled))) {
      current.item.controller.abort(new PreparedReadRevoked());
      this.directories.delete(name);
      current = undefined;
    }
    if (!current) {
      const prepared = this.prepared.get(path);
      const matches = prepared?.directoryScope === scope && !prepared.controller.signal.aborted && (reusable || !prepared.settled);
      if (matches) this.prepared.delete(path);
      const item = matches ? prepared : this.open(path, false);
      current = { scope, item };
      this.directories.set(name, current);
    }
    try {
      const value = await this.joinShared(path, current.item, signal);
      return decode(structuredClone(value));
    } catch (error) {
      if ((!signal.aborted || current.item.users === 0) && this.directories.get(name) === current) this.directories.delete(name);
      throw error;
    }
  }

  private open(path: string, prepared: boolean, directoryScope?: string): SharedRead {
    const controller = new AbortController();
    const work = this.request(path, controller.signal);
    const item: SharedRead = { work, controller, generation: this.generation, directoryScope,
      preparationGeneration: this.preparationGeneration, prepared, users: 0, settled: false };
    const finish = () => { item.settled = true; };
    void work.then(finish, finish);
    return item;
  }
  private startShared(path: string, prepared: boolean, directoryScope?: string): SharedRead {
    const item = this.open(path, prepared, directoryScope);
    this.shared.set(path, item);
    const finish = () => { if (this.shared.get(path) === item) this.shared.delete(path); };
    void item.work.then(finish, finish);
    return item;
  }
  private async joinShared(path: string, item: SharedRead, signal?: AbortSignal): Promise<unknown> {
    item.users++;
    try {
      const scope = signal ? AbortSignal.any([signal, item.controller.signal]) : item.controller.signal;
      const value = await abortable(item.work, scope);
      scope.throwIfAborted();
      if (item.generation !== this.generation || item.prepared && item.preparationGeneration !== this.preparationGeneration)
        throw new PreparedReadRevoked();
      return value;
    } finally {
      if (--item.users === 0 && !item.settled) {
        item.controller.abort();
        if (this.shared.get(path) === item) this.shared.delete(path);
        if (this.prepared.get(path) === item) this.prepared.delete(path);
      }
    }
  }
}
