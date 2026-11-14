import { reactive } from 'vue';
import { ControlClient, ControlFailure } from '../control/client.js';
import { interfaceSnapshot, type InterfaceSnapshot, type InterfaceValues } from '../control/interface.js';

type Pending = {
  id: string;
  revision: number;
  values: InterfaceValues;
  state: 'sending' | 'unknown' | 'querying';
  proof?: InterfaceSnapshot;
};
type Change = { pending: Pending | null; error: string };
type State = {
  interface: InterfaceSnapshot | null;
  capabilities: { instanceId: string } | null;
  uncertainOperation: string;
  error: string;
};

export class InterfaceChanges {
  constructor(
    private readonly client: ControlClient,
    private readonly state: State
  ) {}

  private readonly commands = reactive(new Map<string, Change>());

  private key(instance = this.state.capabilities?.instanceId): string {
    return 'interface-command/' + instance;
  }
  current(): Change {
    const key = this.key();
    if (!this.commands.has(key)) this.commands.set(key, { pending: null, error: '' });
    return this.commands.get(key)!;
  }

  observe(snapshot: InterfaceSnapshot): void {
    if (snapshot.revision >= (this.state.interface?.revision ?? -1)) this.state.interface = snapshot;
    const change = this.current(),
      pending = change.pending;
    if (pending && this.matches(snapshot, pending)) {
      pending.proof = snapshot;
      this.accept(change, pending.id);
    }
  }

  async save(values: InterfaceValues, revision: number, id: string): Promise<InterfaceSnapshot> {
    const instance = this.state.capabilities?.instanceId,
      change = this.current();
    if (!instance || change.pending) throw new ControlFailure(409, 'interface_change_pending');
    const pending: Pending = {
      id,
      revision,
      values: {
        ...values,
        dashboardCards: [...values.dashboardCards],
        pinnedSettings: [...values.pinnedSettings],
        collapsedSettings: [...values.collapsedSettings]
      },
      state: 'sending'
    };
    change.pending = pending;
    change.error = '';
    try {
      const saved = await this.client.write(
        '/control/v1/settings/interface/change',
        {
          format: 'nexus.control.interface-change',
          version: 1,
          operationId: id,
          expectedRevision: revision,
          values: pending.values
        },
        (response) => {
          const snapshot = interfaceSnapshot(response);
          if (!this.matches(snapshot, pending)) throw new TypeError('interface_result_unbound');
          return snapshot;
        },
        id
      );
      pending.proof = saved;
      this.accept(change, id);
      if (this.state.capabilities?.instanceId === instance) this.observe(saved);
      return saved;
    } catch (reason) {
      if (pending.proof) return pending.proof;
      if (change.pending?.id === id) {
        if (reason instanceof ControlFailure && reason.uncertain) {
          change.pending.state = 'unknown';
          change.error = '界面设置结果待确认，请查询原操作。';
        } else {
          change.pending = null;
        }
      }
      throw reason;
    }
  }

  async inspect(): Promise<InterfaceSnapshot> {
    const instance = this.state.capabilities?.instanceId,
      change = this.current(),
      pending = change.pending;
    if (pending && pending.state !== 'unknown') throw new Error('interface_change_busy');
    if (pending) pending.state = 'querying';
    try {
      const latest = await this.client.read('/control/v1/settings/interface', interfaceSnapshot);
      if (this.state.capabilities?.instanceId !== instance) throw new Error('control_instance_changed');
      this.observe(latest);
      if (change.pending) change.error = '尚无法确认原操作；选择与操作编号已保留。';
      return latest;
    } finally {
      if (change.pending?.id === pending?.id && change.pending) change.pending.state = 'unknown';
    }
  }

  private matches(value: InterfaceSnapshot, pending: Pending): boolean {
    return (
      value.operationId === pending.id &&
      value.revision === pending.revision + 1 &&
      JSON.stringify(value.values) === JSON.stringify(pending.values)
    );
  }

  private accept(change: Change, id: string): void {
    const pending = change.pending;
    if (pending?.id !== id) return;
    if (pending && this.state.uncertainOperation === pending.id) {
      this.state.uncertainOperation = '';
      this.state.error = '';
    }
    change.pending = null;
    change.error = '';
  }
}
