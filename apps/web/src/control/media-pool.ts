import { ControlClient, boundedBytes } from './client';
import { imageFormat } from './image-format';
interface ImageEntry {
  blob: Blob;
  bytes: number;
  width: number;
  height: number;
  capturedAt: string | undefined;
  references: number;
}
interface ImageRequest {
  controller: AbortController;
  waiters: number;
  work: Promise<ImageEntry>;
}
export interface ImageLease {
  blob: Blob;
  width: number;
  height: number;
  capturedAt?: string;
  release(): void;
}
export class MediaPool {
  private readonly entries = new Map<string, ImageEntry>();
  private readonly pending = new Map<string, ImageRequest>();
  private readonly queue: { enter(): void; cancel(): void; signal: AbortSignal }[] = [];
  private active = 0;
  private encodedBytes = 0;
  private downloadBytes = 0;
  private readonly downloadWaiters = new Set<() => void>();
  private readonly byteWaiters = new Set<() => void>();
  constructor(private readonly client: ControlClient) {}
  get metrics(): Readonly<{ encodedBytes: number; downloadBytes: number; active: number; queued: number }> {
    return { encodedBytes: this.encodedBytes, downloadBytes: this.downloadBytes, active: this.active, queued: this.queue.length };
  }
  async acquire(path: string, signal: AbortSignal): Promise<ImageLease> {
    signal.throwIfAborted();
    let entry = this.entries.get(path);
    if (!entry) {
      let request = this.pending.get(path);
      if (!request) {
        if (this.pending.size >= 256) throw new Error('image_queue_limit');
        const controller = new AbortController();
        request = { controller, waiters: 0, work: this.load(path, controller.signal) };
        this.pending.set(path, request);
      }
      request.waiters++;
      let canceled = false;
      let abort: () => void = () => {};
      const waiting = new Promise<never>((_resolve, reject) => {
        abort = () => {
          canceled = true;
          request!.waiters--;
          if (!request!.waiters) request!.controller.abort();
          reject(signal.reason);
        };
        signal.addEventListener('abort', abort, { once: true });
      });
      try {
        entry = await Promise.race([request.work, waiting]);
        signal.throwIfAborted();
        entry.references++;
      } finally {
        signal.removeEventListener('abort', abort);
        if (!canceled) request.waiters--;
        if (!request.waiters) {
          if (this.pending.get(path) === request) this.pending.delete(path);
          void request.work
            .then((value) => {
              if (!value.references) this.evict(path, value);
            })
            .catch(() => {});
        }
      }
    } else {
      entry.references++;
    }
    let released = false;
    const release = (): void => {
      if (released) return;
      released = true;
      signal.removeEventListener('abort', release);
      entry!.references--;
      if (!entry!.references) this.evict(path, entry!);
    };
    signal.addEventListener('abort', release, { once: true });
    return { blob: entry.blob, width: entry.width, height: entry.height, ...(entry.capturedAt ? { capturedAt: entry.capturedAt } : {}), release };
  }
  private async load(path: string, signal: AbortSignal): Promise<ImageEntry> {
    await this.enter(signal);
    let reserved = 0;
    let response: Response | undefined;
    try {
      signal.throwIfAborted();
      response = await this.client.response(
        path,
        AbortSignal.any([signal, AbortSignal.timeout(15000)])
      );
      const length = response.headers.get('Content-Length');
      const maximum = length === null ? 32 * 1024 * 1024 : Number(length);
      if (!Number.isSafeInteger(maximum) || maximum < 1 || maximum > 32 * 1024 * 1024)
        throw new Error('image_download_length_invalid');
      await this.reserveDownload(maximum, signal);
      reserved = maximum;
      const bytes = await boundedBytes(response, maximum);
      if (length !== null && bytes.length !== maximum) throw new Error('image_download_length_invalid');
      signal.throwIfAborted();
      const { width, height, type } = imageFormat(bytes, response.headers.get('Content-Type'));
      await this.reserveBytes(bytes.length, signal);
      try {
        signal.throwIfAborted();
        const blob = new Blob([bytes as BlobPart], { type });
        const entry = {
          blob,
          bytes: bytes.length,
          width,
          height,
          capturedAt: response.headers.get('X-Captured-At') ?? undefined,
          references: 0
        };
        this.entries.set(path, entry);
        return entry;
      } catch (reason) {
        this.releaseBytes(bytes.length);
        throw reason;
      }
    } finally {
      if (response?.body && !response.body.locked) await response.body.cancel().catch(() => {});
      if (reserved) {
        this.downloadBytes -= reserved;
        for (const enter of [...this.downloadWaiters]) enter();
      }
      this.leave();
    }
  }
  private async reserveDownload(bytes: number, signal: AbortSignal): Promise<void> {
    signal.throwIfAborted();
    if (this.downloadBytes + bytes <= 32 * 1024 * 1024) { this.downloadBytes += bytes; return; }
    await new Promise<void>((resolve, reject) => {
      const remove = () => {
        this.downloadWaiters.delete(enter);
        signal.removeEventListener('abort', abort);
      };
      const enter = () => {
        if (this.downloadBytes + bytes > 32 * 1024 * 1024) return;
        this.downloadBytes += bytes; remove(); resolve();
      };
      const abort = () => { remove(); reject(signal.reason); };
      this.downloadWaiters.add(enter);
      signal.addEventListener('abort', abort, { once: true });
      if (signal.aborted) abort();
    });
  }
  private async reserveBytes(bytes: number, signal: AbortSignal): Promise<void> {
    signal.throwIfAborted();
    if (this.encodedBytes + bytes <= 32 * 1024 * 1024) {
      this.encodedBytes += bytes;
      return;
    }
    await new Promise<void>((resolve, reject) => {
      const remove = () => {
        this.byteWaiters.delete(enter);
        signal.removeEventListener('abort', abort);
      };
      const enter = () => {
        if (this.encodedBytes + bytes > 32 * 1024 * 1024) return;
        this.encodedBytes += bytes;
        remove();
        resolve();
      };
      const abort = () => {
        remove();
        reject(signal.reason);
      };
      this.byteWaiters.add(enter);
      signal.addEventListener('abort', abort, { once: true });
      if (signal.aborted) abort();
    });
  }
  private releaseBytes(bytes: number): void {
    this.encodedBytes -= bytes;
    for (const enter of [...this.byteWaiters]) enter();
  }
  private async enter(signal: AbortSignal): Promise<void> {
    signal.throwIfAborted();
    if (this.active < 4) {
      this.active++;
      return;
    }
    await new Promise<void>((resolve, reject) => {
      const item = {
        signal,
        enter: () => {
          signal.removeEventListener('abort', item.cancel);
          resolve();
        },
        cancel: () => {
          const index = this.queue.indexOf(item);
          if (index >= 0) this.queue.splice(index, 1);
          reject(signal.reason);
        }
      };
      this.queue.push(item);
      signal.addEventListener('abort', item.cancel, { once: true });
      if (signal.aborted) item.cancel();
    });
  }
  private leave(): void {
    while (this.queue.length) {
      const next = this.queue.shift()!;
      if (next.signal.aborted) {
        next.cancel();
        continue;
      }
      next.enter();
      return;
    }
    this.active--;
  }
  private evict(path: string, entry: ImageEntry): void {
    if (this.entries.get(path) !== entry) return;
    this.entries.delete(path);
    this.releaseBytes(entry.bytes);
  }
}
