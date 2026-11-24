import { ControlClient, ControlFailure } from '../control/client';
import { automation, settings, status } from '../control/models';
import { interfaceSnapshot } from '../control/interface';
import type { EventSnapshot } from '../control/event-feed';
import type { HostState } from './host-state';
import type { InterfaceChanges } from './interface-changes';
import { abortable } from '../control/abortable';

export type SnapshotRevisions = { catalog: number; settings: number; appearance: number; mutation: number };
export type SnapshotReads = { settings: number; status: number };
export class SnapshotStreamChanged extends Error {
  constructor() { super('event_stream_changed'); }
}
type ReadPlan = { automation: boolean; settings: boolean; status: boolean; interface: boolean };
type Domain = keyof ReadPlan;
type PendingSnapshot = {
  revision: number | undefined;
  authority: AbortSignal;
  controller: AbortController;
  signal: AbortSignal;
  ticket: number;
  work: Promise<void>;
  owners: number;
  settled: boolean;
};
const revision = (value: unknown): number | null =>
  typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null;

export class HostSnapshots {
  private stream = '';
  private statusMutation = -1;
  private runtimeSettingsRevision = -1;
  private readonly reads = { automation: 0, settings: 0, status: 0, interface: 0 };
  private readonly committed = { settings: 0, status: 0 };
  private readonly pending = new Map<Domain, PendingSnapshot>();
  private generation = new AbortController();

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
  get settingsRevision(): number {
    return this.runtimeSettingsRevision;
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
      (!value || this.settingsRevision >= value.settings) &&
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
    if (newStream) {
      this.endGeneration(new SnapshotStreamChanged());
      this.statusMutation = -1;
      this.runtimeSettingsRevision = -1;
    }
    return { accepted: true, newStream };
  }

  async read(plan: ReadPlan, signal?: AbortSignal, target?: Partial<SnapshotRevisions> | null): Promise<SnapshotReads> {
    signal?.throwIfAborted();
    const work: Promise<void>[] = [];
    const issued = { settings: 0, status: 0 };
    const add = (domain: Domain, revision: number | undefined, read: (ticket: number, owner: AbortSignal) => Promise<void>) => {
      const request = this.acquire(domain, revision, read);
      work.push(this.join(request, signal));
      return request.ticket;
    };
    if (plan.automation) add('automation', target?.catalog, (ticket, owner) => this.readAutomation(ticket, owner));
    if (plan.settings) issued.settings = add('settings', target?.settings, (ticket, owner) => this.readSettings(ticket, owner));
    if (plan.status) issued.status = add('status', target?.mutation, (ticket, owner) => this.readStatus(ticket, owner, target?.mutation));
    if (plan.interface) add('interface', target?.appearance, (ticket, owner) => this.readInterface(ticket, owner));
    await Promise.all(work);
    signal?.throwIfAborted();
    return issued;
  }

  async refresh(signal?: AbortSignal): Promise<void> {
    await this.read({ automation: true, settings: true, status: true, interface: true }, signal);
  }

  async refreshCatalog(accepted: number, signal?: AbortSignal): Promise<void> {
    const target = () => Math.max(accepted, revision(this.state.events?.catalogRevision) ?? 0);
    const current = () => this.state.automation?.catalogRevision ?? -1;
    for (let attempt = 0; attempt < 2 && current() < target(); attempt++) {
      await this.read({ automation: true, settings: false, status: false, interface: false }, signal, { catalog: target() });
    }
    signal?.throwIfAborted();
    if (current() < target()) throw new Error('event_state_changed');
  }

  async refreshSettings(accepted?: number, signal?: AbortSignal): Promise<NonNullable<HostState['settings']>['saved']> {
    const plan = { automation: false, settings: true, status: false, interface: false };
    const target = () => Math.max(accepted ?? 0, revision(this.state.events?.settingsRevision) ?? 0);
    const issued = accepted === undefined ? (await this.read(plan, signal)).settings : 0;
    const fresh = () => this.settingsRead >= issued && this.settingsRevision >= target();
    for (let attempt = 0; attempt < 2 && !fresh(); attempt++)
      await this.read(plan, signal, { settings: target() });
    signal?.throwIfAborted();
    if (!fresh() || !this.state.settings) throw new Error('event_state_changed');
    return this.state.settings.saved;
  }

  reset(): void {
    this.endGeneration();
    this.reads.automation++;
    this.reads.settings++;
    this.reads.status++;
    this.reads.interface++;
    this.committed.settings = 0;
    this.committed.status = 0;
    this.stream = '';
    this.statusMutation = -1;
    this.runtimeSettingsRevision = -1;
    this.state.revision = 0;
    this.state.events = null;
    this.state.automation = null;
    this.state.settings = null;
    this.state.status = null;
    this.state.interface = null;
  }

  private endGeneration(reason?: unknown): void {
    this.generation.abort(reason);
    this.generation = new AbortController();
    this.pending.clear();
  }

  private acquire(domain: Domain, revision: number | undefined,
    read: (ticket: number, signal: AbortSignal) => Promise<void>): PendingSnapshot {
    const authority = this.client.authoritySignal;
    const current = this.pending.get(domain);
    if (revision !== undefined && current?.revision === revision && current.authority === authority && !current.signal.aborted)
      return current;
    const controller = new AbortController();
    const signal = AbortSignal.any([controller.signal, authority, this.generation.signal]);
    const ticket = ++this.reads[domain];
    const work = read(ticket, signal);
    const request: PendingSnapshot = { revision, authority, controller, signal, ticket, work, owners: 0, settled: false };
    this.pending.set(domain, request);
    const finish = () => {
      request.settled = true;
      if (this.pending.get(domain) === request) this.pending.delete(domain);
    };
    void work.then(finish, finish);
    return request;
  }

  private async join(request: PendingSnapshot, owner?: AbortSignal): Promise<void> {
    request.owners++;
    let released = false;
    const release = () => {
      if (released) return;
      released = true;
      if (--request.owners === 0 && !request.settled) request.controller.abort();
    };
    owner?.addEventListener('abort', release, { once: true });
    if (owner?.aborted) release();
    try {
      const signal = owner ? AbortSignal.any([owner, request.signal]) : request.signal;
      await abortable(request.work, signal);
      signal.throwIfAborted();
    } finally {
      owner?.removeEventListener('abort', release);
      release();
    }
  }

  private async readAutomation(ticket: number, signal?: AbortSignal): Promise<void> {
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
      this.runtimeSettingsRevision = value.saved.revision;
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

  private async readInterface(ticket: number, signal?: AbortSignal): Promise<void> {
    const value = await this.client.read('/control/v1/settings/interface', interfaceSnapshot, signal);
    signal?.throwIfAborted();
    if (ticket === this.reads.interface) this.interfaces.observe(value);
  }
}
