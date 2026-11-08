import { ControlClient, ControlFailure } from '../control/client';
import { automation, settings, status } from '../control/models';
import { interfaceSnapshot } from '../control/interface';
import type { EventSnapshot } from '../control/event-feed';
import type { HostState } from './host-state';
import type { InterfaceChanges } from './interface-changes';

export type SnapshotRevisions = { catalog: number; settings: number; appearance: number; mutation: number };
export type SnapshotReads = { settings: number; status: number };
type ReadPlan = { automation: boolean; settings: boolean; status: boolean; interface: boolean };
const revision = (value: unknown): number | null =>
  typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null;

export class HostSnapshots {
  private stream = '';
  private statusMutation = -1;
  private readonly reads = { automation: 0, settings: 0, status: 0, interface: 0 };
  private readonly committed = { settings: 0, status: 0 };

  constructor(
    private readonly client: ControlClient,
    private readonly state: HostState,
    private readonly interfaces: InterfaceChanges
  ) {}

  get streamId(): string {
    return this.stream;
  }
  get mutation(): number {
    return this.statusMutation;
  }
  get settingsRead(): number {
    return this.committed.settings;
  }
  get statusRead(): number {
    return this.committed.status;
  }

  revisions(snapshot: EventSnapshot): SnapshotRevisions | null {
    const data = snapshot.state;
    const catalog = revision(data.catalogRevision),
      saved = revision(data.settingsRevision);
    const appearance = revision(data.interfaceRevision),
      mutation = revision(data.mutationSequence);
    return catalog === null || saved === null || appearance === null || mutation === null
      ? null
      : { catalog, settings: saved, appearance, mutation };
  }

  aligned(value: SnapshotRevisions | null): boolean {
    return (
      !!this.state.automation &&
      (!value || this.state.automation.catalogRevision >= value.catalog) &&
      !!this.state.settings &&
      (!value || this.state.settings.saved.revision >= value.settings) &&
      !!this.state.interface &&
      (!value || this.state.interface.revision >= value.appearance) &&
      !!this.state.status &&
      (!value || this.statusMutation >= value.mutation)
    );
  }

  observe(snapshot: EventSnapshot): { accepted: boolean; newStream: boolean } {
    const newStream = this.stream !== snapshot.streamId;
    if (!newStream && snapshot.sequence < this.state.revision) return { accepted: false, newStream: false };
    if (!newStream && snapshot.sequence === this.state.revision) return { accepted: true, newStream: false };
    this.client.invalidatePrepared();
    this.stream = snapshot.streamId;
    this.state.revision = snapshot.sequence;
    this.state.events = snapshot.state;
    if (newStream) this.statusMutation = -1;
    return { accepted: true, newStream };
  }

  async read(plan: ReadPlan, signal?: AbortSignal, mutation?: number): Promise<SnapshotReads> {
    const work: Promise<void>[] = [];
    const issued = { settings: 0, status: 0 };
    if (plan.automation) work.push(this.readAutomation(signal));
    if (plan.settings) {
      issued.settings = ++this.reads.settings;
      work.push(this.readSettings(issued.settings, signal));
    }
    if (plan.status) {
      issued.status = ++this.reads.status;
      work.push(this.readStatus(issued.status, signal, mutation));
    }
    if (plan.interface) work.push(this.readInterface(signal));
    await Promise.all(work);
    signal?.throwIfAborted();
    return issued;
  }

  async refresh(signal?: AbortSignal): Promise<void> {
    await this.read({ automation: true, settings: true, status: true, interface: true }, signal);
  }

  reset(): void {
    this.reads.automation++;
    this.reads.settings++;
    this.reads.status++;
    this.reads.interface++;
    this.committed.settings = 0;
    this.committed.status = 0;
    this.stream = '';
    this.statusMutation = -1;
    this.state.revision = 0;
    this.state.events = null;
    this.state.automation = null;
    this.state.settings = null;
    this.state.status = null;
    this.state.interface = null;
  }

  private async readAutomation(signal?: AbortSignal): Promise<void> {
    const ticket = ++this.reads.automation;
    const value = await this.client.read('/control/v1/automation', automation, signal);
    signal?.throwIfAborted();
    if (
      ticket === this.reads.automation &&
      value.catalogRevision >= (this.state.automation?.catalogRevision ?? -1)
    )
      this.state.automation = value;
  }

  private async readSettings(ticket: number, signal?: AbortSignal): Promise<void> {
    const value = await this.client.read('/control/v1/settings', settings, signal);
    signal?.throwIfAborted();
    if (
      ticket === this.reads.settings &&
      value.saved.revision >= (this.state.settings?.saved.revision ?? -1)
    ) {
      this.state.settings = value;
      this.committed.settings = ticket;
    }
  }

  private async readStatus(ticket: number, signal?: AbortSignal, mutation?: number): Promise<void> {
    const value = await this.client.read('/control/v1/status', status, signal);
    signal?.throwIfAborted();
    if (ticket !== this.reads.status) return;
    if (this.state.capabilities && value.instanceId !== this.state.capabilities.instanceId)
      throw new ControlFailure(409, 'control_instance_changed');
    this.state.status = value;
    this.committed.status = ticket;
    if (mutation !== undefined) this.statusMutation = mutation;
  }

  private async readInterface(signal?: AbortSignal): Promise<void> {
    const ticket = ++this.reads.interface;
    const value = await this.client.read('/control/v1/settings/interface', interfaceSnapshot, signal);
    signal?.throwIfAborted();
    if (ticket === this.reads.interface) this.interfaces.observe(value);
  }
}
