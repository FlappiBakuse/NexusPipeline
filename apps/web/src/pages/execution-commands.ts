import { createIdentity } from '../control/identity';
import { ControlClient, ControlFailure, objectValue } from '../control/client';
import type { ActiveRun } from '../control/execution';

interface ExecutionCommand {
  id: string;
  kind: 'start' | 'cancel';
  body: Record<string, unknown>;
  target?: { operationId: string; runId: string; attemptId: string };
}

interface ExecutionCommandState {
  pending: ExecutionCommand | null;
  sending: boolean;
  acceptedRunId?: string;
}

export interface ExecutionCommandContext {
  client: ControlClient;
  state: { capabilities: { instanceId: string } | null; uncertainOperation: string };
  drafts: Map<string, Record<string, unknown>>;
  dirty: Set<string>;
  can(capability: string): boolean;
  report(error: unknown): void;
}

export class ExecutionCommands {
  private readonly instance: string | undefined;
  private readonly key: string;
  readonly state: ExecutionCommandState;

  constructor(private readonly store: ExecutionCommandContext) {
    this.instance = store.state.capabilities?.instanceId;
    this.key = 'execution-command/' + this.instance;
    if (!store.drafts.has(this.key)) store.drafts.set(this.key, { pending: null, sending: false });
    this.state = store.drafts.get(this.key) as unknown as ExecutionCommandState;
  }

  private current(): boolean {
    return !!this.instance && this.instance === this.store.state.capabilities?.instanceId;
  }

  async start(selection: Record<string, unknown>): Promise<boolean> {
    return await this.send({ id: createIdentity(), kind: 'start', body: structuredClone(selection) });
  }

  async cancel(run: ActiveRun): Promise<boolean> {
    return await this.send({
      id: createIdentity(),
      kind: 'cancel',
      target: { operationId: run.operationId, runId: run.runId, attemptId: run.attemptId },
      body: {
        format: 'nexus.control.execution-cancellation',
        version: 1,
        runId: run.runId,
        attemptId: run.attemptId
      }
    });
  }

  private async send(command: ExecutionCommand): Promise<boolean> {
    if (!this.current() || this.state.pending) return false;
    this.state.pending = command;
    this.state.sending = true;
    this.store.dirty.add(this.key);
    const path =
      command.kind === 'start'
        ? '/control/v1/execution/start/' + command.id
        : '/control/v1/execution/' + command.target!.operationId + '/cancel/' + command.id;
    try {
      await this.store.client.write(
        path,
        command.body,
        (value) => {
          const result = objectValue(value);
          const acceptance = objectValue(result.acceptance);
          if (
            acceptance.operationId !== command.id ||
            !['accepted', 'completed'].includes(String(acceptance.status)) ||
            !this.uuid(result.runId) ||
            !this.uuid(result.attemptId) ||
            (command.target &&
              (result.runId !== command.target.runId || result.attemptId !== command.target.attemptId))
          )
            throw new Error('execution_result_unbound');
          if (command.kind === 'start') this.state.acceptedRunId = String(result.runId);
          return result;
        },
        command.id
      );
      return this.finish(command);
    } catch (error) {
      this.store.report(error);
      if (this.current() && error instanceof ControlFailure && !error.uncertain) this.clear(command);
      return false;
    } finally {
      this.state.sending = false;
    }
  }

  async inspect(): Promise<boolean> {
    const command = this.state.pending;
    if (!command || this.state.sending || !this.current() || !this.store.can('operations.read')) return false;
    this.state.sending = true;
    try {
      const result = await this.store.client.read('/control/v1/operations/' + command.id, objectValue);
      const kinds =
        command.kind === 'start' ? ['execution.start'] : ['execution.cancel', 'execution.retry.cancel'];
      if (result.operationId !== command.id || !kinds.includes(String(result.kind)))
        throw new Error('execution_operation_unbound');
      if (!['accepted', 'completed'].includes(String(result.status))) return false;
      return this.finish(command);
    } catch (error) {
      this.store.report(error);
      return false;
    } finally {
      this.state.sending = false;
    }
  }

  private finish(command: ExecutionCommand): boolean {
    if (!this.current() || this.state.pending?.id !== command.id) return false;
    this.clear(command);
    return true;
  }

  private clear(command: ExecutionCommand): void {
    if (this.state.pending?.id !== command.id) return;
    this.state.pending = null;
    this.store.dirty.delete(this.key);
    if (this.store.state.uncertainOperation === command.id) this.store.state.uncertainOperation = '';
  }

  private uuid(value: unknown): boolean {
    return (
      typeof value === 'string' &&
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)
    );
  }
}
