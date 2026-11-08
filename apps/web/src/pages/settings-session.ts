import { createIdentity } from '../control/identity';
import { computed, onBeforeUnmount, reactive, watch } from 'vue';
import type { AppStore } from '../app/store';
import { copyDraft } from '../app/draft-copy';
import { ControlFailure, objectValue } from '../control/client';
import { completedSettings, savedSettings } from '../control/settings-command';
import { settings } from '../control/models';
import type { ControlHostSettings, ControlHostSettingsValues } from '../control/wire.generated';

interface SettingsDraft {
  draft: ControlHostSettingsValues;
  baseline: string;
  revision: number;
  pending: ControlHostSettings | null;
  busy: boolean;
  uncertain: boolean;
  error: string;
}

export function useSettingsSession(store: AppStore, valid: () => boolean) {
  const saved = store.state.settings!.saved;
  const instanceId = store.state.capabilities!.instanceId;
  const key = 'settings/host/' + instanceId;
  const isCurrent = (): boolean => store.state.capabilities?.instanceId === instanceId;
  let state = store.drafts.get(key) as unknown as SettingsDraft | undefined;
  if (!state) {
    state = reactive({
      draft: copyDraft(saved.values),
      baseline: JSON.stringify(saved.values),
      revision: saved.revision,
      pending: null,
      busy: false,
      uncertain: false,
      error: ''
    });
    store.drafts.set(key, state as unknown as Record<string, unknown>);
  }
  const session = state;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let disposed = false;
  const dirty = computed(() => JSON.stringify(session.draft) !== session.baseline);
  const conflict = computed(
    () => !!store.state.settings && session.revision !== store.state.settings.saved.revision
  );
  const locked = computed(() => session.uncertain || session.busy);
  watch(
    dirty,
    (changed) => {
      if (changed || session.pending) store.dirty.add(key);
      else store.dirty.delete(key);
    },
    { immediate: true }
  );
  watch(
    () => store.state.settings,
    (snapshot) => {
      if (snapshot && isCurrent() && !dirty.value && !session.pending && !session.busy) {
        session.draft = copyDraft(snapshot.saved.values);
        session.baseline = JSON.stringify(snapshot.saved.values);
        session.revision = snapshot.saved.revision;
      }
    }
  );
  onBeforeUnmount(() => {
    disposed = true;
    clearTimeout(timer);
  });

  function schedule(): void {
    clearTimeout(timer);
    if (!session.uncertain && !conflict.value && !session.busy) timer = setTimeout(() => void save(), 350);
  }
  function patch(values: Partial<ControlHostSettingsValues>): void {
    if (!isCurrent() || session.uncertain) return;
    session.draft = { ...session.draft, ...values };
    session.error = '';
    schedule();
  }
  async function read(): Promise<void> {
    if (!isCurrent()) return;
    const snapshot = await store.client.read('/control/v1/settings', settings);
    if (isCurrent() && snapshot.saved.revision >= (store.state.settings?.saved.revision || 0))
      store.state.settings = snapshot;
  }
  async function save(): Promise<void> {
    if (
      !isCurrent() ||
      !dirty.value ||
      session.busy ||
      session.pending ||
      conflict.value ||
      !store.can('settings.write')
    )
      return;
    if (!valid()) return;
    const body: ControlHostSettings = {
      format: 'nexus.control.host-settings',
      version: 1,
      operationId: createIdentity(),
      expectedRevision: session.revision,
      values: copyDraft(session.draft)
    };
    session.pending = body;
    session.busy = true;
    session.error = '';
    try {
      await store.client.write(
        '/control/v1/settings',
        body,
        (value) => savedSettings(value, body),
        body.operationId
      );
      accept(body);
      await read();
    } catch (reason) {
      if (!(reason instanceof ControlFailure && reason.code === 'settings_invalid')) store.report(reason);
      if (session.pending && reason instanceof ControlFailure && reason.uncertain) session.uncertain = true;
      else if (session.pending) session.pending = null;
      session.error = session.uncertain
        ? '保存结果待确认，原操作已保留。'
        : reason instanceof ControlFailure && reason.code === 'settings_invalid'
          ? '设置校验未通过，请检查必填字段和取值范围。'
        : reason instanceof ControlFailure && reason.code === 'startup_elevation_required'
          ? '请以管理员身份启动软件后设置开机自启；当前设置未保存。'
        : reason instanceof Error
          ? reason.message
          : '设置保存失败，草稿已保留。';
      if (reason instanceof ControlFailure && reason.status === 409) {
        try {
          await read();
        } catch {
          /* The frozen revision stays unchanged until an explicit decision. */
        }
      }
    } finally {
      session.busy = false;
    }
    if (!disposed && !session.error && dirty.value) schedule();
  }
  function accept(body: ControlHostSettings): void {
    if (store.state.uncertainOperation === body.operationId) {
      store.state.uncertainOperation = '';
      store.state.error = '';
    }
    session.baseline = JSON.stringify(body.values);
    session.revision = body.expectedRevision + 1;
    session.pending = null;
    session.uncertain = false;
    session.error = '';
    if (!dirty.value) store.dirty.delete(key);
  }
  async function inspect(): Promise<void> {
    const body = session.pending;
    if (!isCurrent() || !body || !session.uncertain || session.busy) return;
    session.busy = true;
    try {
      const operation = await store.client.read('/control/v1/operations/' + body.operationId, objectValue);
      if (!completedSettings(operation, body)) {
        session.error = '原操作尚未完成，请保留草稿并稍后查询。';
        return;
      }
      accept(body);
      await read();
    } catch (reason) {
      store.report(reason);
      session.error = '无法确认原操作，草稿与操作编号已保留。';
    } finally {
      session.busy = false;
    }
  }
  function loadLatest(): void {
    const latest = store.state.settings?.saved;
    if (!isCurrent() || !latest || session.busy || session.pending) return;
    if (store.state.error === session.error) store.state.error = '';
    session.draft = copyDraft(latest.values);
    session.baseline = JSON.stringify(latest.values);
    session.revision = latest.revision;
    session.error = '';
  }
  return { session, dirty, conflict, locked, patch, save, inspect, loadLatest };
}
