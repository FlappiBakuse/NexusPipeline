import { onBeforeUnmount, reactive, watch, type Ref } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import { ControlFailure, arrayValue, boundedInteger, objectValue, stringValue } from '../control/client';
import { executionRun, type ActiveStep } from '../control/execution';
import { historyImage, type AttemptFacts, type HistoryImage } from '../control/records';

export type HistoryLog = Record<string, unknown> & {
  key: { stepId: string; role: string; invocationOrdinal?: number };
};
export const historyLogId = (log: HistoryLog): string =>
  JSON.stringify([log.key.stepId, log.key.role, log.key.invocationOrdinal ?? null]);
type Kind = 'logs' | 'images' | 'report';
type Status = { loaded: boolean; busy: boolean; requested: boolean; indexing: boolean; error: string };
type Catalog<T> = Status & { items: T[]; next: number | null };
type Props = { store: RecordsStore; runId: string; attemptId: string; stepIds?: string[]; userId?: string };
const status = (): Status => ({ loaded: false, busy: false, requested: true, indexing: false, error: '' });
const catalog = <T>(): Catalog<T> => ({ ...status(), items: [], next: -1 });

export function useAttemptResources(props: Props, active: Ref<boolean>) {
  const state = reactive({ logs: catalog<HistoryLog>(), images: catalog<HistoryImage>(),
    report: { ...status(), steps: [] as ActiveStep[], facts: undefined as AttemptFacts | undefined } });
  const owners: Partial<Record<Kind, AbortController>> = {};
  const timers: Partial<Record<Kind, ReturnType<typeof setTimeout>>> = {};
  const base = () => '/control/v1/records/' + props.runId + '/attempts/' + props.attemptId;
  const scope = () => JSON.stringify([props.runId, props.attemptId, props.stepIds, props.userId,
    props.store.state.capabilities?.instanceId, props.store.state.capabilities?.hostVersion,
    props.store.state.capabilities?.capabilities]);

  function stop(kind: Kind): void {
    clearTimeout(timers[kind]); delete timers[kind];
    owners[kind]?.abort(); delete owners[kind]; state[kind].busy = false;
  }
  function reset(): void {
    for (const kind of ['logs', 'images', 'report'] as const) stop(kind);
    Object.assign(state.logs, catalog<HistoryLog>());
    Object.assign(state.images, catalog<HistoryImage>());
    Object.assign(state.report, { ...status(), steps: [], facts: undefined });
    if (active.value) startRequested();
  }
  function startRequested(): void {
    for (const kind of ['logs', 'images', 'report'] as const)
      if (state[kind].requested) void load(kind);
  }
  async function load(kind: Kind): Promise<void> {
    const target = state[kind];
    if (!active.value || target.busy) return;
    if (kind === 'report' ? state.report.loaded : state[kind].loaded && state[kind].next === null) return;
    if (kind !== 'report' && !props.store.can(kind === 'logs' ? 'records.logs.read' : 'records.images.read')) {
      target.loaded = true; target.requested = false; state[kind].next = null; return;
    }
    clearTimeout(timers[kind]); delete timers[kind];
    const owner = new AbortController(); owners[kind] = owner;
    const expectedScope = scope();
    const current = () => active.value && owners[kind] === owner && scope() === expectedScope;
    const guard = () => { if (!current()) owner.abort(); owner.signal.throwIfAborted(); };
    target.busy = true; target.requested = true; target.error = ''; target.indexing = false;
    try {
      if (kind === 'report') await loadReport(owner.signal, guard);
      else await loadCatalog(kind, owner.signal, guard);
      guard();
      target.loaded = true; target.requested = false;
    } catch (reason) {
      if (!current()) owner.abort();
      if (!owner.signal.aborted) {
        target.indexing = reason instanceof ControlFailure && reason.code === 'history_index_pending';
        target.error = target.indexing ? '' : reason instanceof ControlFailure && reason.code === 'history_index_failed'
          ? '历史索引补齐失败，请检查诊断后重启服务' : reason instanceof Error ? reason.message : String(reason);
        if (target.indexing && active.value) timers[kind] = setTimeout(() => void load(kind), 1500);
      }
    } finally {
      if (owners[kind] === owner) { delete owners[kind]; target.busy = false; }
    }
  }
  async function loadReport(signal: AbortSignal, guard: () => void): Promise<void> {
    const report = await props.store.client.read(base() + '/report', executionRun, signal);
    guard();
    if (report.runId !== props.runId || report.attemptId !== props.attemptId) throw Error('history_report_identity_invalid');
    state.report.steps = report.steps.filter(step => !props.stepIds || props.stepIds.includes(step.stepId));
    state.report.facts = report.accounts?.find(account => account.userId === props.userId)?.facts;
  }
  async function loadCatalog(kind: 'logs' | 'images', signal: AbortSignal, guard: () => void): Promise<void> {
    const after = state[kind].next!;
    const result = await props.store.client.read(base() + '/' + kind + '?limit=100&afterPosition=' + after, objectValue, signal);
    guard();
    const rows = arrayValue(result.items, objectValue);
    if (rows.length > 100) throw Error('history_catalog_limit_invalid');
    let last = after;
    for (const row of rows) {
      const position = boundedInteger(row.position, 0, 4095), key = objectValue(row.key);
      if (position <= last || key.runId !== props.runId || key.attemptId !== props.attemptId)
        throw Error('history_catalog_identity_invalid');
      last = position;
    }
    const next = result.nextPosition == null ? null : boundedInteger(result.nextPosition, 0, 4095);
    if (next !== null && (next !== last || next <= after)) throw Error('history_catalog_cursor_invalid');
    if (kind === 'logs') {
      const items = rows.map(item => {
        const key = objectValue(item.key);
        return { ...item, key: { stepId: stringValue(key.stepId), role: stringValue(key.role),
          ...(key.invocationOrdinal == null ? {} : { invocationOrdinal: boundedInteger(key.invocationOrdinal, 1, 99) }) } };
      });
      guard();
      state.logs.items.push(...items.filter(item => !props.stepIds || props.stepIds.includes(item.key.stepId)));
    } else {
      const items = rows.map(item => historyImage(item, props.runId, props.attemptId));
      guard();
      state.images.items.push(...items.filter(item => !props.stepIds || props.stepIds.includes(item.key.stepId)));
    }
    state[kind].next = next;
  }
  watch(scope, reset, { immediate: true });
  watch(active, visible => {
    if (visible) startRequested();
    else for (const kind of ['logs', 'images', 'report'] as const) stop(kind);
  }, { immediate: true });
  onBeforeUnmount(() => { for (const kind of ['logs', 'images', 'report'] as const) stop(kind); });
  return { state, load };
}
