import { computed, ref, shallowRef, type InjectionKey } from 'vue';
import type { AppStore } from '../app/store';
import { ControlFailure } from '../control/client';
import { localExtensionSnapshot, type LocalExtensionCandidate, type LocalExtensionSnapshot } from '../control/extensions';

type NoticeKind = 'removed' | 'waiting' | 'installed' | 'discovered';
interface LocalNotice { key: string; kind: NoticeKind; names: string[] }

const path = '/control/v1/extensions/local';

export function createLocalPlugins(store: AppStore) {
  const available = ref(false);
  const items = shallowRef<LocalExtensionCandidate[]>([]);
  const refreshing = ref(false);
  const scanning = ref(false);
  const revision = ref(0);
  const error = ref('');
  const queue = shallowRef<LocalNotice[]>([]);
  const toast = computed(() => queue.value[0]);
  const seen = new Set<string>();
  let timer: ReturnType<typeof setTimeout> | undefined;
  let readWork: Promise<void> | undefined;
  let scanWork: Promise<void> | undefined;
  let owner: AbortController | undefined;
  let authority: AbortSignal | undefined;
  let instance = '';
  let published = '';
  let waitingForRemoval = false;

  function enqueue(key: string, kind: NoticeKind, names: string[]): void {
    if (!names.length || seen.has(key)) return;
    seen.add(key);
    queue.value = [...queue.value, { key, kind, names }];
  }
  function enqueueNewNames(prefix: string, kind: NoticeKind, names: string[]): void {
    const counts = new Map<string, number>();
    const fresh: string[] = [];
    const keys: string[] = [];
    for (const name of names) {
      const count = (counts.get(name) ?? 0) + 1;
      counts.set(name, count);
      const key = JSON.stringify([prefix, kind, name, count]);
      if (seen.has(key)) continue;
      fresh.push(name);
      keys.push(key);
      seen.add(key);
    }
    enqueue(JSON.stringify(keys), kind, fresh);
  }

  function accept(snapshot: LocalExtensionSnapshot): void {
    available.value = true;
    const notice = snapshot.notice;
    const prefix = instance + '/' + notice?.batchId + '/';
    if (notice) {
      enqueueNewNames(prefix, 'removed', notice.removed);
      enqueueNewNames(prefix, 'waiting', notice.waiting);
    }
    const discovered = snapshot.items.filter(item => item.state === 'pendingApproval' &&
      !seen.has(instance + '/candidate/' + item.discoveryId));
    enqueue(instance + '/discovered/' + discovered.map(item => item.discoveryId).join(','),
      'discovered', discovered.map(item => item.name));
    for (const item of discovered) seen.add(instance + '/candidate/' + item.discoveryId);
    if (notice) enqueueNewNames(prefix, 'installed', notice.installed);
    items.value = snapshot.items;
    const committed = notice && (notice.removed.length || notice.installed.length)
      ? JSON.stringify([notice.batchId, notice.removed, notice.installed]) : '';
    if (committed !== published || (scanning.value && !snapshot.scanning)) revision.value++;
    published = committed;
    scanning.value = snapshot.scanning;
    waitingForRemoval = !!notice?.waiting.length;
  }

  function reset(): void {
    clearTimeout(timer);
    owner?.abort();
    readWork = scanWork = undefined;
    available.value = refreshing.value = scanning.value = false;
    error.value = '';
    items.value = [];
    queue.value = [];
    published = '';
    waitingForRemoval = false;
  }

  function scope(): AbortSignal | undefined {
    const current = store.can('extensions.read') ? store.state.capabilities?.instanceId || '' : '';
    if (!owner || current !== instance || authority !== store.client.authoritySignal) {
      reset();
      instance = current;
      authority = store.client.authoritySignal;
      owner = new AbortController();
    }
    return instance && !owner.signal.aborted ? AbortSignal.any([authority!, owner.signal]) : undefined;
  }

  function poll(signal: AbortSignal): void {
    if (signal.aborted) return;
    clearTimeout(timer);
    if (scanning.value || waitingForRemoval) timer = setTimeout(() => void refresh(), 1200);
  }

  function refresh(): Promise<void> {
    const signal = scope();
    if (!signal) return Promise.resolve();
    return scanWork ?? (readWork ??= (async () => {
      try {
        const snapshot = await store.client.read(path, localExtensionSnapshot, signal);
        signal.throwIfAborted();
        accept(snapshot);
      } catch (reason) {
        if (signal.aborted) return;
        if (reason instanceof ControlFailure && reason.status === 403) reset();
      } finally {
        if (!signal.aborted) readWork = undefined;
        poll(signal);
      }
    })());
  }

  function scan(): Promise<void> {
    const signal = scope();
    if (!signal || !available.value || !store.can('extensions.write')) return Promise.resolve();
    return scanWork ??= (async () => {
      refreshing.value = true;
      error.value = '';
      let checkProgress = false;
      try {
        await readWork;
        signal.throwIfAborted();
        if (!available.value) return;
        const snapshot = await store.client.write(path + '/scan', {}, localExtensionSnapshot, undefined, signal);
        signal.throwIfAborted();
        accept(snapshot);
        revision.value++;
      } catch (reason) {
        if (signal.aborted) return;
        error.value = reason instanceof Error ? reason.message : '扫描本地插件失败。';
        checkProgress = reason instanceof ControlFailure && reason.uncertain;
      } finally {
        if (!signal.aborted) {
          scanWork = undefined;
          refreshing.value = false;
        }
        if (checkProgress) void refresh();
        else poll(signal);
      }
    })();
  }

  return { available, items, busy: computed(() => refreshing.value || scanning.value), revision, error, toast, refresh, scan,
    closeToast: () => { queue.value = queue.value.slice(1); }, dispose: reset };
}

export const localPluginsKey: InjectionKey<ReturnType<typeof createLocalPlugins>> = Symbol('local plugins');
