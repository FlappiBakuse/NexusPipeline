import { createIdentity } from '../control/identity';
import { computed, onBeforeUnmount, reactive, watch } from 'vue';
import type { AppStore } from '../app/store';
import { ControlFailure } from '../control/client';
import { interfaceSnapshot, type InterfaceCategory, type InterfaceValues } from '../control/interface';
import { copyDraft } from '../app/draft-copy';

interface ViewDraft {
  values: InterfaceValues;
  revision: number;
  edited: boolean;
  busy: boolean;
  pending: { id: string; revision: number; values: InterfaceValues } | null;
  error: string;
}

export function useSettingsView(store: AppStore, categories: readonly InterfaceCategory[]) {
  const initial = store.state.interface!;
  const instanceId = store.state.capabilities!.instanceId;
  const key = 'settings/view/' + instanceId;
  const isCurrent = (): boolean => store.state.capabilities?.instanceId === instanceId;
  let stored = store.drafts.get(key) as unknown as ViewDraft | undefined;
  if (!stored) {
    stored = reactive({
      values: copyDraft(initial.values),
      revision: initial.revision,
      edited: false,
      busy: false,
      pending: null,
      error: ''
    });
    store.drafts.set(key, stored as unknown as Record<string, unknown>);
  }
  const state = stored;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const conflict = computed(
    () => state.edited && !!store.state.interface && state.revision !== store.state.interface.revision
  );
  const pinned = computed(() => state.values.pinnedSettings);
  watch(
    () => store.state.interface,
    (snapshot) => {
      if (snapshot && isCurrent() && !state.edited && !state.pending && !state.busy) {
        state.values = copyDraft(snapshot.values);
        state.revision = snapshot.revision;
      }
    }
  );
  onBeforeUnmount(() => clearTimeout(timer));

  function schedule(): void {
    state.edited = true;
    store.dirty.add(key);
    clearTimeout(timer);
    if (!state.pending && !state.busy) timer = setTimeout(() => void save(), 120);
  }
  function choose(id: InterfaceCategory): void {
    if (!isCurrent() || state.pending || state.busy) return;
    state.values.pinnedSettings = [id];
    state.values.collapsedSettings = categories.filter((item) => item !== id);
    schedule();
  }
  function toggle(id: InterfaceCategory): void {
    if (!isCurrent() || state.pending || state.busy) return;
    const current = state.values.collapsedSettings;
    state.values.collapsedSettings = current.includes(id)
      ? current.filter((item) => item !== id)
      : [...current, id];
    schedule();
  }
  function setLocale(value: InterfaceValues['locale']): void {
    if (!isCurrent() || state.pending || state.busy) return;
    state.values.locale = value;
    schedule();
  }
  function isExpanded(id: InterfaceCategory): boolean {
    return !state.values.collapsedSettings.includes(id);
  }
  async function save(): Promise<void> {
    if (
      !isCurrent() ||
      !state.edited ||
      state.busy ||
      state.pending ||
      conflict.value ||
      !store.can('settings.write')
    )
      return;
    const submitted = { id: createIdentity(), revision: state.revision, values: copyDraft(state.values) };
    state.pending = submitted;
    state.busy = true;
    state.error = '';
    try {
      const saved = await store.saveInterface(submitted.values, submitted.revision, submitted.id);
      if (saved.operationId !== submitted.id)
        throw new ControlFailure(0, 'interface_result_unbound', undefined, submitted.id, true);
      accept(saved);
    } catch (reason) {
      store.report(reason);
      if (!(reason instanceof ControlFailure && reason.uncertain)) state.pending = null;
      state.error = state.pending ? '视图保存结果待确认，请查询原操作。' : '视图设置保存失败，选择已保留。';
      if (reason instanceof ControlFailure && reason.status === 409) await read();
    } finally {
      state.busy = false;
    }
  }
  function accept(snapshot: NonNullable<typeof store.state.interface>): void {
    if (state.pending && store.state.uncertainOperation === state.pending.id) {
      store.state.uncertainOperation = '';
      store.state.error = '';
    }
    state.values = copyDraft(snapshot.values);
    state.revision = snapshot.revision;
    state.edited = false;
    state.pending = null;
    state.error = '';
    store.dirty.delete(key);
  }
  async function read(): Promise<void> {
    if (!isCurrent()) return;
    try {
      const latest = await store.inspectInterface();
      if (isCurrent() && latest.revision >= (store.state.interface?.revision || 0))
        store.state.interface = latest;
    } catch (reason) {
      store.report(reason);
      state.error = '视图设置读取失败，选择已保留。';
    }
  }
  async function checkPending(): Promise<void> {
    const submitted = state.pending;
    if (!isCurrent() || !submitted || state.busy) return;
    state.busy = true;
    try {
      const latest = await store.inspectInterface();
      if (!isCurrent()) return;
      if (latest.revision >= (store.state.interface?.revision || 0)) store.state.interface = latest;
      if (
        latest.operationId === submitted.id &&
        latest.revision === submitted.revision + 1 &&
        JSON.stringify(latest.values) === JSON.stringify(submitted.values)
      )
        accept(latest);
      else state.error = '尚无法确认原操作；选择与操作编号已保留。';
    } catch (reason) {
      store.report(reason);
      state.error = '视图设置读取失败，选择已保留。';
    } finally {
      state.busy = false;
    }
  }
  function loadLatest(): void {
    if (isCurrent() && store.state.interface && !state.pending && !state.busy) accept(store.state.interface);
  }
  return { state, pinned, conflict, choose, toggle, setLocale, isExpanded, save, checkPending, loadLatest };
}
