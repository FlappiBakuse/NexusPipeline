import { onBeforeUnmount, reactive, watch } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import { accountSummaries, type AccountSummary } from '../control/account-summary';

export function useAccountSummaries(store: AutomationStore, visible: () => boolean) {
  const state = reactive({ items: new Map<string, AccountSummary>(), now: Date.now(), error: '', busy: false });
  let owner: AbortController | undefined, timer: ReturnType<typeof setInterval> | undefined;
  let again = false;
  const expired = new Set<string>();
  const allowed = () => ['automation.read', 'control.status.read', 'records.read', 'scheduling.read'].every(store.can);
  async function refresh(): Promise<void> {
    if (!visible() || !allowed()) return;
    if (owner) { again = true; return; }
    const operation = new AbortController(); owner = operation; state.busy = true;
    const instance = store.state.capabilities?.instanceId;
    const catalog = store.state.automation;
    if (!catalog) { owner = undefined; state.busy = false; return; }
    try {
      const response = await store.client.read('/control/v1/automation/users/summary',
        value => accountSummaries(value, catalog.bundle.users.map(user => user.id)), operation.signal);
      if (operation.signal.aborted || !visible() || store.state.capabilities?.instanceId !== instance ||
          response.catalogRevision !== store.state.automation?.catalogRevision) return;
      state.items = new Map(response.items.map(item => [item.userId, item])); state.error = '';
      const identities = new Set(response.items.map(identity));
      for (const value of expired) if (!identities.has(value)) expired.delete(value);
    } catch (error) {
      if (!operation.signal.aborted) state.error = error instanceof Error ? error.message : '账号摘要读取失败。';
    } finally {
      if (owner === operation) { owner = undefined; state.busy = false; }
      if (again && !operation.signal.aborted) { again = false; void refresh(); }
    }
  }
  const identity = (item: AccountSummary) => [item.userId, item.queueId, item.nextRunAt].join('/');
  function stop(): void {
    owner?.abort(); owner = undefined; again = false; state.busy = false;
    clearInterval(timer); timer = undefined;
  }
  watch(() => [visible(), store.state.capabilities, store.state.automation?.catalogRevision,
    store.state.events?.attempts, store.state.events?.completedRuns, store.state.events?.settingsRevision], () => {
    if (!visible()) { stop(); return; }
    if (!allowed()) { stop(); state.items.clear(); state.error = '账号摘要不可读取；缺少所需权限。'; return; }
    if (!timer) timer = setInterval(() => {
      state.now = Date.now();
      let due = false;
      for (const item of state.items.values())
        if (item.nextRunAt && Date.parse(item.nextRunAt) <= state.now && !expired.has(identity(item))) {
          expired.add(identity(item)); due = true;
        }
      if (due) void refresh();
    }, 1000);
    state.now = Date.now(); void refresh();
  }, { immediate: true });
  onBeforeUnmount(stop);
  return state;
}
