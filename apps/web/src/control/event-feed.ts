import { ControlClient, integerValue, objectValue, stringValue } from './client';

export interface EventSnapshot {
  instanceId: string;
  streamId: string;
  sequence: number;
  state: Record<string, unknown>;
}
export function eventSnapshot(value: unknown, expectedInstance?: string): EventSnapshot {
  const row = objectValue(value);
  const instanceId = stringValue(row.instanceId);
  if (expectedInstance && instanceId !== expectedInstance) throw new Error('event_instance_changed');
  return {
    instanceId,
    streamId: stringValue(row.streamId),
    sequence: integerValue(row.sequence),
    state: objectValue(row.state)
  };
}
export class EventFeed {
  private owner: AbortController | undefined;
  private readonly listeners = new Set<(snapshot: EventSnapshot, fresh: boolean) => void>();
  private current?: EventSnapshot;
  private timer?: ReturnType<typeof setTimeout>;
  constructor(
    private readonly client: ControlClient,
    private readonly report: (error: unknown) => void,
    private readonly expectedInstance?: () => string | undefined
  ) {}
  subscribe(listener: (snapshot: EventSnapshot, fresh: boolean) => void): () => void {
    this.listeners.add(listener);
    if (this.current && (!this.expectedInstance || this.current.instanceId === this.expectedInstance()))
      listener(this.current, false);
    if (!this.owner && !document.hidden) this.start();
    return () => {
      this.listeners.delete(listener);
      if (!this.listeners.size) this.stop();
    };
  }
  visibility(visible: boolean): void {
    if (visible && this.listeners.size) this.start();
    else this.stop();
  }
  stop(): void {
    this.owner?.abort();
    this.owner = undefined;
    clearTimeout(this.timer);
  }
  private start(): void {
    if (this.owner) return;
    const owner = new AbortController();
    this.owner = owner;
    void this.consume(owner.signal).catch((error) => {
      if (owner.signal.aborted) return;
      this.report(error);
      this.owner = undefined;
      this.timer = setTimeout(() => this.start(), 2000);
    });
  }
  private async consume(signal: AbortSignal): Promise<void> {
    const snapshot = await this.client.read(
      '/control/v1/events/snapshot',
      (value) => eventSnapshot(value, this.expectedInstance?.()),
      signal
    );
    signal.throwIfAborted();
    this.publish(snapshot, true);
    const response = await this.client.response(
      '/control/v1/events?' +
        new URLSearchParams({
          streamId: snapshot.streamId,
          afterSequence: String(snapshot.sequence)
        }),
      signal
    );
    const reader = response.body?.getReader();
    if (!reader) throw new Error('event_stream_missing');
    const decoder = new TextDecoder();
    let pending = '';
    try {
      while (!signal.aborted) {
        const part = await reader.read();
        signal.throwIfAborted();
        if (part.done) throw new Error('event_stream_ended');
        pending += decoder.decode(part.value, { stream: true }).replace(/\r\n/g, '\n');
        if (pending.length > 1024 * 1024) throw new Error('event_gap');
        let index: number;
        let count = 0;
        while ((index = pending.indexOf('\n\n')) >= 0) {
          if (++count > 256) throw new Error('event_gap');
          const frame = pending.slice(0, index);
          pending = pending.slice(index + 2);
          const data = frame
            .split('\n')
            .filter((line) => line.startsWith('data:'))
            .map((line) => line.slice(5).trimStart())
            .join('\n');
          if (!data) continue;
          const event = objectValue(JSON.parse(data));
          if (event.kind === 'resync') throw new Error('event_gap');
          const sequence = integerValue(event.sequence);
          const subject = objectValue(event.subject);
          if (
            event.format !== 'nexus.event' ||
            event.version !== 1 ||
            event.kind !== 'state' ||
            event.topic !== 'host.state' ||
            subject.type !== 'instance' ||
            subject.id !== snapshot.instanceId ||
            event.streamId !== snapshot.streamId ||
            event.instanceId !== snapshot.instanceId ||
            sequence !== (this.current?.sequence ?? snapshot.sequence) + 1
          )
            throw new Error('event_gap');
          this.publish(
            {
              instanceId: snapshot.instanceId,
              streamId: snapshot.streamId,
              sequence,
              state: objectValue(event.data)
            },
            false
          );
        }
      }
    } finally {
      await reader.cancel();
      reader.releaseLock();
    }
  }
  private publish(snapshot: EventSnapshot, fresh: boolean): void {
    this.current = snapshot;
    for (const listener of this.listeners) listener(snapshot, fresh);
  }
}
