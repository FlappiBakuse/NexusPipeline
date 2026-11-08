import { abortable } from './abortable';

type SharedRead = {
  work: Promise<unknown>;
  controller: AbortController;
  generation: number;
  preparationGeneration: number;
  prepared: boolean;
  users: number;
  settled: boolean;
};
type Decoder<T> = (value: unknown) => T;

export class ControlReads {
  private readonly reading = new Set<Promise<unknown>>();
  private readonly prepared = new Map<string, SharedRead>();
  private readonly shared = new Map<string, SharedRead>();
  private generation = 0;
  private preparationGeneration = 0;

  constructor(private readonly request: (path: string, signal?: AbortSignal) => Promise<unknown>) {}

  async settled(signal: AbortSignal): Promise<void> {
    while (this.reading.size) await abortable(Promise.allSettled([...this.reading]).then(() => {}), signal);
  }
  clear(): void {
    this.generation++;
    this.invalidatePrepared();
    for (const item of this.shared.values()) item.controller.abort(new Error('prepared_read_revoked'));
    this.shared.clear();
  }
  invalidatePrepared(): void {
    this.preparationGeneration++;
    this.prepared.clear();
    for (const [path, item] of this.shared) if (item.prepared) {
      item.controller.abort(new Error('prepared_read_revoked'));
      this.shared.delete(path);
    }
  }
  async prepare<T = unknown>(path: string, signal: AbortSignal, decode?: Decoder<T>): Promise<T> {
    signal.throwIfAborted();
    let item = this.prepared.get(path);
    if (item?.controller.signal.aborted) { this.prepared.delete(path); item = undefined; }
    if (!item) {
      if (this.prepared.size >= 32) throw new Error('prepared_read_capacity');
      item = this.shared.get(path);
      if (!item) {
        if (this.shared.size >= 32) throw new Error('prepared_read_capacity');
        item = this.startShared(path, true);
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
    const work = this.request(path, signal);
    this.reading.add(work);
    try { return decode(await work); }
    finally { this.reading.delete(work); }
  }
  private startShared(path: string, prepared: boolean): SharedRead {
    const controller = new AbortController();
    const work = this.request(path, controller.signal);
    const item: SharedRead = { work, controller, generation: this.generation,
      preparationGeneration: this.preparationGeneration, prepared, users: 0, settled: false };
    this.shared.set(path, item);
    this.reading.add(work);
    const finish = () => {
      item.settled = true;
      this.reading.delete(work);
      if (this.shared.get(path) === item) this.shared.delete(path);
    };
    void work.then(finish, finish);
    return item;
  }
  private async joinShared(path: string, item: SharedRead, signal?: AbortSignal): Promise<unknown> {
    item.users++;
    try {
      const value = await (signal ? abortable(item.work, signal) : item.work);
      signal?.throwIfAborted();
      if (item.generation !== this.generation || item.prepared && item.preparationGeneration !== this.preparationGeneration)
        throw new Error('prepared_read_revoked');
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
