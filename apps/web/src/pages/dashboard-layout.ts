import { createIdentity } from '../control/identity';
import { computed, reactive, toRefs } from 'vue';
import type { AppStore } from '../app/store';
import { copyDraft } from '../app/draft-copy';
import { ControlFailure } from '../control/client.js';
import {
  dashboardCards,
  dashboardDefaults,
  type DashboardCard,
  type InterfaceSnapshot,
  type InterfaceValues
} from '../control/interface.js';

interface LayoutSession {
  draft: DashboardCard[] | null;
  baseline: InterfaceSnapshot | null;
  pendingId: string;
  pendingValues: InterfaceValues | null;
  editing: boolean;
  busy: boolean;
  error: string;
  notice: string;
}

class DashboardLayoutOwner {
  readonly session: LayoutSession;
  private readonly instance: string;
  private readonly key: string;
  constructor(private readonly store: AppStore, private readonly defaults: () => DashboardCard[]) {
    this.instance = store.state.capabilities!.instanceId;
    this.key = 'dashboard-layout/' + this.instance;
    if (!store.drafts.has(this.key))
      store.drafts.set(
        this.key,
        reactive({
          draft: null,
          baseline: null,
          pendingId: '',
          pendingValues: null,
          editing: false,
          busy: false,
          error: '',
          notice: ''
        })
      );
    this.session = store.drafts.get(this.key) as unknown as LayoutSession;
  }
  private current(): boolean {
    return this.store.state.capabilities?.instanceId === this.instance;
  }
  private mark(): void {
    const state = this.session;
    const dirty =
      state.pendingId ||
      (state.draft &&
        JSON.stringify(state.draft) !==
          JSON.stringify(state.baseline?.revision === 0 ? this.defaults() : dashboardCards(state.baseline?.values.dashboardCards || [])));
    if (dirty) this.store.dirty.add(this.key);
    else this.store.dirty.delete(this.key);
  }
  async read(): Promise<void> {
    if (!this.current() || this.session.busy || !this.store.can('settings.read')) return;
    this.session.busy = true;
    try {
      await this.store.inspectInterface();
      this.session.error = '';
    } catch (reason) {
      if (this.current()) {
        this.store.report(reason);
        this.session.error = '布局读取失败。';
      }
    } finally {
      this.session.busy = false;
    }
  }
  async begin(): Promise<void> {
    if (!this.current() || this.session.busy) return;
    if (this.session.pendingId || this.session.draft) {
      this.session.editing = true;
      return;
    }
    if (!this.store.state.interface) await this.read();
    const snapshot = this.store.state.interface;
    if (!this.current() || !snapshot || this.store.interfaceChange.pending) return;
    this.session.baseline = copyDraft(snapshot);
    this.session.draft = snapshot.revision === 0 ? this.defaults() : dashboardCards(snapshot.values.dashboardCards);
    this.session.editing = true;
    this.session.error = '';
    this.session.notice = '';
    this.mark();
  }
  cancel(): void {
    this.session.editing = false;
    if (!this.session.pendingId) {
      this.session.draft = null;
      this.session.error = '';
    }
    this.mark();
  }
  change(cards: DashboardCard[]): void {
    if (this.session.busy || this.session.pendingId) return;
    this.session.draft = dashboardCards(cards);
    this.mark();
  }
  rebase(keep: boolean): void {
    const snapshot = this.store.state.interface;
    if (!this.current() || !snapshot || this.session.pendingId || this.session.busy) return;
    this.session.baseline = copyDraft(snapshot);
    if (!keep) this.session.draft = snapshot.revision === 0 ? this.defaults() : dashboardCards(snapshot.values.dashboardCards);
    this.session.error = '';
    this.mark();
  }
  private accept(notice: string): void {
    this.session.pendingId = '';
    this.session.pendingValues = null;
    this.session.draft = null;
    this.session.editing = false;
    this.session.error = '';
    this.session.notice = notice;
    this.mark();
  }
  async save(): Promise<void> {
    const state = this.session,
      baseline = state.baseline;
    if (
      !this.current() ||
      !state.draft ||
      !baseline ||
      state.busy ||
      state.pendingId ||
      this.store.state.interface?.revision !== baseline.revision ||
      !this.store.can('settings.write')
    )
      return;
    const values = { ...copyDraft(baseline.values), dashboardCards: [...state.draft] },
      id = createIdentity();
    state.pendingId = id;
    state.pendingValues = values;
    state.busy = true;
    state.error = '';
    this.mark();
    let conflict = false;
    try {
      await this.store.saveInterface(values, baseline.revision, id);
      this.accept('布局已保存。');
    } catch (reason) {
      if (!(reason instanceof ControlFailure && reason.uncertain)) {
        state.pendingId = '';
        state.pendingValues = null;
      }
      state.error = state.pendingId
        ? '布局结果待确认；请只读查询，勿重复保存。'
        : reason instanceof Error
          ? reason.message
          : '布局保存失败，草稿仍在。';
      conflict = reason instanceof ControlFailure && reason.status === 409;
      if (this.current()) this.store.report(reason);
    } finally {
      state.busy = false;
      this.mark();
    }
    if (conflict) await this.read();
  }
  async inspect(): Promise<void> {
    const state = this.session,
      id = state.pendingId,
      expected = state.baseline?.revision,
      values = state.pendingValues;
    if (!this.current() || !id || state.busy || !values) return;
    state.busy = true;
    try {
      const latest = await this.store.inspectInterface();
      if (!this.current() || state.pendingId !== id) return;
      if (
        latest.operationId === id &&
        latest.revision === expected! + 1 &&
        JSON.stringify(latest.values) === JSON.stringify(values)
      )
        this.accept('布局已确认保存。');
      else state.error = '目前无法证明保存已完成；草稿和操作 ID 已保留。';
    } catch (reason) {
      if (this.current()) this.store.report(reason);
    } finally {
      state.busy = false;
    }
  }
}

export function useDashboardLayout(store: AppStore, contributions: () => DashboardCard[] = () => []) {
  const defaults = () => [...contributions(), ...dashboardDefaults];
  const owner = new DashboardLayoutOwner(store, defaults),
    state = owner.session;
  const { draft, editing, busy, error, notice, pendingId } = toRefs(state);
  const layout = computed(() => store.state.interface);
  return {
    layout,
    draft,
    layoutOpen: editing,
    layoutBusy: busy,
    layoutError: error,
    layoutNotice: notice,
    pendingLayoutId: pendingId,
    cards: computed(() => layout.value?.revision === 0 ? defaults() : dashboardCards(layout.value?.values.dashboardCards || [])),
    layoutChanged: computed(() => !!state.draft && layout.value?.revision !== state.baseline?.revision),
    beginLayout: () => owner.begin(),
    cancelLayout: () => owner.cancel(),
    saveLayout: () => owner.save(),
    checkLayout: () => owner.inspect(),
    readLayout: () => owner.read(),
    rebase: () => owner.rebase(true),
    loadLatest: () => owner.rebase(false),
    changeLayout: (cards: DashboardCard[]) => owner.change(cards),
    resetLayout: () => owner.change(defaults())
  };
}
