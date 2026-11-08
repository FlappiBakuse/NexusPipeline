import { ControlClient, ControlFailure } from '../control/client';
import { capabilities, type Capabilities } from '../control/models';
import { EventFeed, eventSnapshot, type EventSnapshot } from '../control/event-feed';
import type { HostState } from './host-state';
import { HostSnapshots, type SnapshotReads, type SnapshotRevisions } from './host-snapshots';
import type { AppRoute } from './navigation';

const sameAccess = (left: Capabilities | null, right: Capabilities): boolean =>
  !!left &&
  left.instanceId === right.instanceId &&
  left.hostVersion === right.hostVersion &&
  left.capabilities.length === right.capabilities.length &&
  right.capabilities.every((capability) => left.capabilities.includes(capability));

export class AppSession {
  private readonly feed: EventFeed;
  private navigationOwner: AbortController | undefined;
  private updateOwner: AbortController | undefined;
  private updateWork: Promise<void> | undefined;
  private stopFeed: (() => void) | undefined;
  private accessRequiresRefresh = false;
  private pendingRoute: AppRoute | undefined;

  constructor(
    private readonly client: ControlClient,
    private readonly state: HostState,
    private readonly snapshots: HostSnapshots,
    private readonly report: (error: unknown) => void,
    private readonly readRoute: () => AppRoute
  ) {
    this.feed = new EventFeed(
      client,
      (error) => {
        this.client.invalidatePrepared();
        state.feedError = error instanceof Error ? error.message : 'connection_lost';
      },
      () => state.capabilities?.instanceId
    );
    document.addEventListener('visibilitychange', this.visibility);
  }

  async navigate(page: AppRoute, preserveLocation = false, initialAccess?: Capabilities, signal?: AbortSignal,
    initialSnapshot?: EventSnapshot): Promise<void> {
    this.navigationOwner?.abort();
    const owner = new AbortController();
    this.navigationOwner = owner;
    const cancel = () => owner.abort(signal?.reason);
    signal?.addEventListener('abort', cancel, { once: true });
    if (signal?.aborted) cancel();
    this.pendingRoute = page;
    this.state.busy = true;
    try {
      const { snapshot, access } = await this.readAccess(owner.signal, initialAccess, initialSnapshot);
      const plan = this.prepare(snapshot, access);
      await this.synchronize(page, snapshot, plan, owner.signal);
      if (this.navigationOwner !== owner) return;
      this.state.page = page;
      this.accessRequiresRefresh = false;
      if (!preserveLocation && this.readRoute() !== page) history.pushState(null, '', '#/' + page);
      this.pendingRoute = undefined;
      this.state.error = '';
      if (!this.stopFeed && this.state.automation) this.subscribe();
    } catch (error) {
      if (!owner.signal.aborted) this.report(error);
      if (initialAccess) throw error;
    } finally {
      signal?.removeEventListener('abort', cancel);
      if (this.navigationOwner === owner) this.state.busy = false;
    }
  }

  async start(signal?: AbortSignal): Promise<void> {
    const owner = new AbortController();
    const cancel = () => owner.abort(signal?.reason);
    signal?.addEventListener('abort', cancel, { once: true });
    if (signal?.aborted) cancel();
    try {
      const { snapshot, access } = await this.readAccess(owner.signal);
      owner.signal.throwIfAborted();
      if (snapshot.instanceId !== access.instanceId) throw new ControlFailure(409, 'control_instance_changed');
      if (this.state.capabilities && this.state.capabilities.instanceId !== access.instanceId) {
        this.stopFeed?.();
        this.stopFeed = undefined;
        this.feed.stop();
        this.navigationOwner?.abort();
        this.updateOwner?.abort();
        this.updateWork = undefined;
        this.client.clearPrepared();
        this.snapshots.reset();
      }
      if (!sameAccess(this.state.capabilities, access)) {
        this.client.clearPrepared();
        this.state.capabilities = access;
        this.accessRequiresRefresh = true;
      }
      await this.navigate(this.pendingRoute ?? this.readRoute(), !this.pendingRoute, access, owner.signal, snapshot);
    } catch (error) {
      owner.abort();
      throw error;
    } finally {
      signal?.removeEventListener('abort', cancel);
    }
  }

  dispose(): void {
    this.stopFeed?.();
    this.feed.stop();
    this.navigationOwner?.abort();
    this.updateOwner?.abort();
    document.removeEventListener('visibilitychange', this.visibility);
  }

  private async readAccess(signal: AbortSignal, initialAccess?: Capabilities,
    initialSnapshot?: EventSnapshot): Promise<{ snapshot: EventSnapshot; access: Capabilities }> {
    for (let attempt = 0; attempt < 2; attempt++) {
      const before = this.snapshots.streamId;
      const [snapshot, access] = await Promise.all([
        attempt === 0 && initialSnapshot ? Promise.resolve(initialSnapshot) :
          this.client.read('/control/v1/events/snapshot', eventSnapshot, signal),
        attempt === 0 && initialAccess ? Promise.resolve(initialAccess) : this.client.read('/control/v1/capabilities', capabilities, signal)
      ]);
      signal.throwIfAborted();
      if (
        before === this.snapshots.streamId ||
        !this.snapshots.streamId ||
        snapshot.streamId === this.snapshots.streamId
      )
        return { snapshot, access };
    }
    throw new Error('event_state_changed');
  }

  private prepare(
    snapshot: EventSnapshot,
    access: Capabilities
  ): { current: SnapshotRevisions | null; full: boolean } {
    if (
      snapshot.instanceId !== access.instanceId ||
      (this.state.capabilities && access.instanceId !== this.state.capabilities.instanceId)
    )
      throw new ControlFailure(409, 'control_instance_changed');
    const accessChanged = !sameAccess(this.state.capabilities, access);
    if (accessChanged) {
      this.client.clearPrepared();
      this.state.capabilities = access;
      this.accessRequiresRefresh = true;
    }
    const restart =
      !!this.stopFeed && !!this.snapshots.streamId && this.snapshots.streamId !== snapshot.streamId;
    if (restart) {
      this.stopFeed?.();
      this.stopFeed = undefined;
      this.updateOwner?.abort();
      this.updateWork = undefined;
    }
    const observed = this.snapshots.observe(snapshot);
    if (restart) this.subscribe();
    const current = this.snapshots.revisions(
      observed.accepted ? snapshot : { ...snapshot, state: this.state.events || {} }
    );
    const full =
      observed.newStream ||
      accessChanged ||
      this.accessRequiresRefresh ||
      !current ||
      !this.state.automation ||
      !this.state.settings ||
      !this.state.status ||
      !this.state.interface;
    return { current, full };
  }

  private async synchronize(
    page: AppRoute,
    snapshot: EventSnapshot,
    plan: { current: SnapshotRevisions | null; full: boolean },
    signal: AbortSignal
  ): Promise<void> {
    const { current, full } = plan;
    const issued = await this.snapshots.read(
      {
        automation: full || current!.catalog !== this.state.automation?.catalogRevision,
        settings: full || page === 'settings' || current!.settings !== this.state.settings?.saved.revision,
        status:
          full ||
          page === 'dashboard' ||
          page === 'settings' ||
          current!.mutation !== this.snapshots.mutation,
        interface: full || current!.appearance !== this.state.interface?.revision
      },
      signal,
      current?.mutation
    );
    for (let attempt = 0; attempt < 2; attempt++) {
      if (this.snapshots.aligned(this.latest(snapshot)) && this.fresh(page, issued)) break;
      if (this.updateWork) await this.updateWork.catch(() => {});
      signal.throwIfAborted();
      const pending = this.latest(snapshot);
      if (this.snapshots.aligned(pending) && this.fresh(page, issued)) break;
      await this.snapshots.read(
        {
          automation:
            !this.state.automation || !pending || this.state.automation.catalogRevision < pending.catalog,
          settings:
            !this.state.settings ||
            !pending ||
            this.state.settings.saved.revision < pending.settings ||
            (page === 'settings' && this.snapshots.settingsRead < issued.settings),
          status:
            !this.state.status ||
            !pending ||
            this.snapshots.mutation < pending.mutation ||
            ((page === 'dashboard' || page === 'settings') && this.snapshots.statusRead < issued.status),
          interface: !this.state.interface || !pending || this.state.interface.revision < pending.appearance
        },
        signal,
        pending?.mutation
      );
    }
    if (!this.snapshots.aligned(this.latest(snapshot)) || !this.fresh(page, issued))
      throw new Error('event_state_changed');
  }

  private latest(snapshot: EventSnapshot): SnapshotRevisions | null {
    return this.state.events ? this.snapshots.revisions({ ...snapshot, state: this.state.events }) : null;
  }

  private fresh(page: AppRoute, issued: SnapshotReads): boolean {
    return (
      (page !== 'settings' || this.snapshots.settingsRead >= issued.settings) &&
      ((page !== 'dashboard' && page !== 'settings') || this.snapshots.statusRead >= issued.status)
    );
  }

  private subscribe(): void {
    let initial = true;
    this.stopFeed = this.feed.subscribe((snapshot, fresh) => {
      if (snapshot.instanceId !== this.state.capabilities?.instanceId) {
        this.report(new Error('event_instance_changed'));
        return;
      }
      if (!fresh && this.snapshots.streamId && snapshot.streamId !== this.snapshots.streamId) return;
      const observed = this.snapshots.observe(snapshot);
      if (!observed.accepted) return;
      this.state.feedError = '';
      this.updateOwner?.abort();
      const owner = new AbortController();
      this.updateOwner = owner;
      const current = this.snapshots.revisions(snapshot);
      // Navigation already aligned the first stream snapshot; reconnects still refresh live status.
      const full = (fresh && !initial) || observed.newStream || !current;
      initial = false;
      const work = this.snapshots
        .read(
          {
            automation: full || current!.catalog !== this.state.automation?.catalogRevision,
            settings: full || current!.settings !== this.state.settings?.saved.revision,
            status: full || current!.mutation !== this.snapshots.mutation,
            interface: full || current!.appearance !== this.state.interface?.revision
          },
          owner.signal,
          current?.mutation
        )
        .then(() => {});
      this.updateWork = work;
      void work
        .catch((error) => {
          if (!owner.signal.aborted) this.report(error);
        })
        .finally(() => {
          if (this.updateWork === work) this.updateWork = undefined;
        });
    });
  }

  private readonly visibility = (): void => {
    this.state.visible = !document.hidden;
    this.feed.visibility(this.state.visible);
    if (!this.state.visible) { this.client.invalidatePrepared(); this.updateOwner?.abort(); }
  };
}
