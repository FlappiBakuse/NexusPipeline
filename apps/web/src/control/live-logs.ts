import { ControlClient, ControlFailure, objectValue, arrayValue, stringValue, integerValue } from './client';
export interface LogIdentity {
  runId: string;
  attemptId: string;
  stepId: string;
  role: string;
}
export interface LogWindow {
  text: string;
  sequence: number;
  omitted: boolean;
  connected: boolean;
}
interface LogOwner {
  key: LogIdentity;
  value: LogWindow;
  listeners: Set<(value: LogWindow) => void>;
  controller: AbortController;
  timer: ReturnType<typeof setTimeout> | undefined;
  rotation: ReturnType<typeof setTimeout> | undefined;
  completed: boolean;
}
export class LiveLogs {
  private readonly owners = new Map<string, LogOwner>();
  private readonly active = new Set<LogOwner>();
  constructor(private readonly client: ControlClient) {}
  subscribe(key: LogIdentity, listener: (value: LogWindow) => void): () => void {
    const id = JSON.stringify(key);
    let owner = this.owners.get(id);
    if (!owner) {
      owner = {
        key,
        value: { text: '', sequence: 0, omitted: false, connected: false },
        listeners: new Set(),
        controller: new AbortController(),
        timer: undefined,
        rotation: undefined,
        completed: false
      };
      this.owners.set(id, owner);
    }
    owner.listeners.add(listener);
    listener(owner.value);
    this.startWaiting();
    let released = false;
    return () => {
      if (released) return;
      released = true;
      owner!.listeners.delete(listener);
      if (owner!.listeners.size) return;
      owner!.controller.abort();
      clearTimeout(owner!.timer);
      clearTimeout(owner!.rotation);
      owner!.value.text = '';
      this.owners.delete(id);
      this.active.delete(owner!);
      this.startWaiting();
    };
  }
  private startWaiting(): void {
    if (this.hasWaiting()) for (const owner of this.active) this.rotate(owner);
    for (const owner of this.owners.values()) {
      if (this.active.size >= 4) return;
      if (owner.completed || this.active.has(owner) || owner.timer) continue;
      owner.controller = new AbortController();
      this.active.add(owner);
      this.start(owner);
    }
  }
  private start(owner: LogOwner): void {
    const controller = owner.controller;
    this.rotate(owner);
    void this.read(owner, controller.signal)
      .catch(async (error) => {
        if (controller.signal.aborted) return;
        if ((error instanceof ControlFailure && error.code === 'resource_unavailable') ||
            (error instanceof Error && error.message === 'log_stream_ended')) {
          try {
            if (await this.durableTail(owner, controller.signal)) { owner.completed = true; return; }
          } catch {
            /* A hook may not have started or committed evidence yet. */
          }
        }
        if (controller.signal.aborted) return;
        owner.value = { ...owner.value, connected: false };
        this.publish(owner);
        owner.timer = setTimeout(() => {
          owner.timer = undefined;
          this.startWaiting();
        }, 2000);
      })
      .finally(() => {
        clearTimeout(owner.rotation);
        owner.rotation = undefined;
        if (owner.controller !== controller) return;
        this.active.delete(owner);
        const id = JSON.stringify(owner.key);
        if (this.owners.get(id) === owner) {
          this.owners.delete(id);
          this.owners.set(id, owner);
        }
        this.startWaiting();
      });
  }
  private rotate(owner: LogOwner): void {
    if (owner.rotation) return;
    owner.rotation = setTimeout(() => {
      owner.rotation = undefined;
      if (this.hasWaiting()) owner.controller.abort();
    }, 2000);
  }
  private async read(owner: LogOwner, signal: AbortSignal): Promise<void> {
    const { key } = owner;
    const path =
      '/control/v1/execution/runs/' +
      key.runId +
      '/attempts/' +
      key.attemptId +
      '/logs/' +
      encodeURIComponent(key.stepId) +
      '/' +
      key.role +
      '/events';
    const response = await this.client.response(path, signal);
    const reader = response.body?.getReader();
    if (!reader) throw new Error('log_stream_missing');
    const utf8 = new TextDecoder();
    const logDecoder = new TextDecoder();
    let pending = '';
    try {
      while (!signal.aborted) {
        const chunk = await reader.read();
        if (chunk.done) throw new Error('log_stream_ended');
        pending += utf8.decode(chunk.value, { stream: true });
        if (pending.length > 1024 * 1024) throw new Error('log_gap');
        let position: number;
        let count = 0;
        while ((position = pending.indexOf('\n\n')) >= 0) {
          if (++count > 256) throw new Error('log_gap');
          const frame = pending.slice(0, position);
          pending = pending.slice(position + 2);
          const line = frame.split('\n').find((value) => value.startsWith('data:'));
          if (!line) continue;
          const event = objectValue(JSON.parse(line.slice(5)));
          const identity = objectValue(event.key);
          if (
            Object.entries(key).some(([field, value]) => identity[field] !== value) ||
            event.kind === 'resync'
          )
            throw new Error('log_gap');
          const data = objectValue(event.data);
          if (event.kind === 'tail') {
            owner.value = { text: '', sequence: 0, omitted: data.prefixOmitted === true, connected: true };
            logDecoder.decode();
            for (const item of arrayValue(data.frames, objectValue))
              this.append(owner, item, logDecoder, false);
            owner.value.sequence = integerValue(data.lastSequence);
          } else if (event.kind === 'frame') this.append(owner, data, logDecoder, true);
          else throw new Error('log_event_invalid');
          this.publish(owner);
        }
      }
    } finally {
      await reader.cancel();
      reader.releaseLock();
    }
  }
  private hasWaiting(): boolean {
    for (const owner of this.owners.values())
      if (!owner.completed && !this.active.has(owner) && !owner.timer) return true;
    return false;
  }
  private async durableTail(owner: LogOwner, signal: AbortSignal): Promise<boolean> {
    const key = owner.key;
    const path =
      '/control/v1/records/' +
      key.runId +
      '/attempts/' +
      key.attemptId +
      '/logs/' +
      encodeURIComponent(key.stepId) +
      '/' +
      key.role +
      '/tail';
    const result = await this.client.read(path, objectValue, signal);
    signal.throwIfAborted();
    const description = objectValue(result.description);
    const identity = objectValue(description.key);
    if (Object.entries(key).some(([field, value]) => identity[field] !== value))
      throw new Error('log_identity_invalid');
    const decoder = new TextDecoder();
    owner.value = { text: '', sequence: 0, omitted: result.prefixOmitted === true, connected: false };
    for (const [index, slice] of arrayValue(result.slices, objectValue).entries())
      this.append(owner, { ...slice, sequence: index + 1 }, decoder, false);
    owner.value = { ...owner.value, connected: false };
    this.publish(owner);
    return description.isComplete === true;
  }
  private append(
    owner: LogOwner,
    value: Record<string, unknown>,
    decoder: TextDecoder,
    requireNext: boolean
  ): void {
    const sequence = integerValue(value.sequence);
    if (requireNext && (sequence !== owner.value.sequence + 1 || value.gapBefore === true))
      throw new Error('log_gap');
    const encoded = stringValue(value.bytes);
    if (encoded.length > 16384) throw new Error('log_frame_limit');
    const bytes = Uint8Array.from(atob(encoded), (letter) => letter.charCodeAt(0));
    let text = owner.value.text + decoder.decode(bytes, { stream: true });
    const lines = text.split('\n');
    let omitted = owner.value.omitted;
    if (lines.length > 500) {
      text = lines.slice(-500).join('\n');
      omitted = true;
    }
    if (text.length > 256 * 1024) {
      text = text.slice(-256 * 1024);
      omitted = true;
    }
    owner.value = { text, sequence, omitted, connected: true };
  }
  private publish(owner: LogOwner): void {
    for (const listener of owner.listeners) listener(owner.value);
  }
}
