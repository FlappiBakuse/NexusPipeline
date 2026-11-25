import { ControlClient, ControlFailure, objectValue, arrayValue, stringValue, integerValue,
  readTransport, closeResponseReader } from './client';
import { transientReadFailure } from './read-retry';
import { LiveLogTail } from './live-log-tail';
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
  error?: string;
}
interface LogOwner {
  key: LogIdentity;
  value: LogWindow;
  listeners: Set<(value: LogWindow) => void>;
  controller: AbortController;
  timer: ReturnType<typeof setTimeout> | undefined;
  rotation: ReturnType<typeof setTimeout> | undefined;
  completed: boolean;
  resyncs: number;
}

class LogResume extends Error {}

function unavailableLog(error: unknown): boolean {
  return error instanceof ControlFailure && error.status === 409 && error.code === 'resource_unavailable';
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
        completed: false,
        resyncs: 0
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
      owner!.value = { ...owner!.value, text: '' };
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
        const ended = error instanceof LogResume && error.message === 'log_stream_ended';
        const resync = error instanceof LogResume && !ended;
        let reconnect = transientReadFailure(error) || unavailableLog(error) || ended || resync && ++owner.resyncs <= 3;
        if (unavailableLog(error) || error instanceof LogResume) {
          try {
            if (await this.durableTail(owner, controller.signal)) { owner.completed = true; return; }
          } catch (tailError) {
            if (!unavailableLog(tailError) && !transientReadFailure(tailError)) {
              reconnect = false;
              error = tailError;
            }
          }
        }
        if (controller.signal.aborted) return;
        if (!reconnect) owner.completed = true;
        owner.value = { ...owner.value, connected: false,
          ...(reconnect ? {} : { error: error instanceof Error ? error.message : 'log_event_invalid' }) };
        this.publish(owner);
        if (!reconnect || controller.signal.aborted) return;
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
    const utf8 = new TextDecoder('utf-8', { fatal: true });
    const logDecoder = new TextDecoder();
    const tail = new LiveLogTail();
    let pending = '';
    let started = false;
    let failure: unknown;
    try {
      while (!signal.aborted) {
        const chunk = await readTransport(() => reader.read(), signal);
        if (chunk.done) {
          if (pending || utf8.decode()) throw new Error('log_event_invalid');
          throw new LogResume('log_stream_ended');
        }
        signal.throwIfAborted();
        pending += utf8.decode(chunk.value, { stream: true });
        let position: number;
        let count = 0;
        while ((position = pending.indexOf('\n\n')) >= 0) {
          if (position > 1024 * 1024) throw new Error('log_frame_limit');
          if (count === 256) {
            await new Promise(resolve => setTimeout(resolve, 0));
            signal.throwIfAborted();
            count = 0;
          }
          count++;
          const frame = pending.slice(0, position);
          pending = pending.slice(position + 2);
          const line = frame.split('\n').find((value) => value.startsWith('data:'));
          if (!line) continue;
          let payload: unknown;
          try { payload = JSON.parse(line.slice(5)); }
          catch { throw new Error('log_event_invalid'); }
          const event = objectValue(payload);
          const identity = objectValue(event.key);
          if (Object.entries(key).some(([field, value]) => identity[field] !== value))
            throw new Error('log_identity_invalid');
          const data = objectValue(event.data);
          if (event.kind === 'resync') throw new LogResume(stringValue(data.reasonCode));
          if (!started && event.kind === 'tail') {
            let value: LogWindow = { text: '', sequence: 0, omitted: data.prefixOmitted === true, connected: true };
            for (const item of arrayValue(data.frames, objectValue))
              value = this.append(value, item, logDecoder, tail);
            const complete = data.isComplete === true;
            if (complete) tail.append(logDecoder.decode());
            owner.value = { ...value, text: tail.text, omitted: value.omitted || tail.omitted,
              sequence: integerValue(data.lastSequence), connected: !complete };
            if (complete) {
              owner.completed = true;
              this.publish(owner);
              return;
            }
            started = true;
          } else if (started && event.kind === 'frame') {
            const sequence = integerValue(data.sequence);
            if (data.gapBefore === true && sequence > owner.value.sequence) throw new LogResume('log_gap');
            if (sequence !== owner.value.sequence + 1) throw new Error('log_gap');
            owner.value = this.append(owner.value, data, logDecoder, tail);
            owner.resyncs = 0;
          } else throw new Error('log_event_invalid');
          this.publish(owner);
        }
        if (pending.length > 1024 * 1024) throw new Error('log_frame_limit');
      }
    } catch (error) {
      failure = error;
      throw error;
    } finally { await closeResponseReader(reader, failure, signal); }
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
    const tail = new LiveLogTail();
    let value: LogWindow = { text: '', sequence: 0, omitted: result.prefixOmitted === true, connected: false };
    for (const [index, slice] of arrayValue(result.slices, objectValue).entries())
      value = this.append(value, { ...slice, sequence: index + 1 }, decoder, tail);
    const complete = description.isComplete === true;
    const remaining = complete ? decoder.decode() : '';
    if (remaining) value = this.appendText(value, tail, remaining);
    owner.value = value;
    this.publish(owner);
    return complete;
  }
  private append(
    previous: LogWindow,
    value: Record<string, unknown>,
    decoder: TextDecoder,
    tail: LiveLogTail
  ): LogWindow {
    const sequence = integerValue(value.sequence);
    const encoded = stringValue(value.bytes);
    if (encoded.length > 16384) throw new Error('log_frame_limit');
    const bytes = Uint8Array.from(atob(encoded), (letter) => letter.charCodeAt(0));
    return this.appendText(previous, tail, decoder.decode(bytes, { stream: true }), sequence);
  }
  private appendText(previous: LogWindow, tail: LiveLogTail, chunk: string, sequence = previous.sequence): LogWindow {
    tail.append(chunk);
    return { ...previous, text: tail.text, sequence, omitted: previous.omitted || tail.omitted };
  }
  private publish(owner: LogOwner): void {
    for (const listener of owner.listeners) listener(owner.value);
  }
}
