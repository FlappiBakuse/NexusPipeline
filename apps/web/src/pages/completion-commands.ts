import { ControlClient, ControlFailure, objectValue } from '../control/client.js';

export interface CompletionContext {
  client: ControlClient;
  drafts: Map<string, Record<string, unknown>>;
  state: { capabilities: { instanceId: string } | null };
  can(capability: string): boolean;
  report(reason: unknown): void;
}
export class CompletionCommands {
  readonly state: { pending: string; busy: boolean };
  private readonly instance: string;
  constructor(private readonly store: CompletionContext) {
    this.instance = store.state.capabilities?.instanceId || '';
    const key = 'completion/' + this.instance;
    if (!store.drafts.has(key)) store.drafts.set(key, { pending: '', busy: false });
    this.state = store.drafts.get(key) as typeof this.state;
  }
  private current(): boolean {
    return !!this.instance && this.store.state.capabilities?.instanceId === this.instance;
  }
  async cancel(id: string): Promise<boolean> {
    if (
      !id ||
      !this.current() ||
      !this.store.can('completion.local') ||
      this.state.pending ||
      this.state.busy
    )
      return false;
    this.state.pending = id;
    this.state.busy = true;
    try {
      await this.store.client.write(
        '/control/v1/local/completion/' + encodeURIComponent(id) + '/cancel',
        null,
        (value) => {
          const row = objectValue(value);
          if (row.cancelled !== true) throw new TypeError('completion_cancel_response_invalid');
          return row;
        },
        id
      );
      return this.current();
    } catch (reason) {
      if (!(reason instanceof ControlFailure && reason.uncertain)) this.state.pending = '';
      this.store.report(reason);
      return false;
    } finally {
      this.state.busy = false;
    }
  }
  observe(operationId: string | null, phase: string | null): void {
    if (this.current() && !this.state.busy && (operationId !== this.state.pending || phase === 'Cancelled'))
      this.state.pending = '';
  }
}
