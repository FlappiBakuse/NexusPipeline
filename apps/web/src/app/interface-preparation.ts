import { nextTick, ref } from 'vue';
import type { AppStore } from './store';
import type { FrontendLoader } from '../extensions/frontend-loader';
import type { FrontendRegistry } from '../extensions/frontend-registry';
import { abortable } from '../control/abortable';
import { preparePages } from './page-preparation';
import { desktop } from './desktop';

export function interfacePreparation(store: AppStore, frontend: FrontendLoader, registry: FrontendRegistry) {
  const preparing = ref(true), surfacesReady = ref(false), failed = ref(false), coreFailed = ref(false);
  const stage = ref('准备界面'), count = ref({ done: 0, total: 0 });
  let current: AbortController | undefined, work: Promise<void> | undefined;
  let presented = false;
  const progress = (done: number, total: number) => {
    count.value = { done, total };
    if (!presented) void desktop?.preparation?.({ stage: stage.value, done, total }).catch(store.report);
  };
  const report = (error: unknown) => { failed.value = true; store.report(error); };
  function prepare(): Promise<void> {
    if (work) return work;
    const owner = new AbortController(); current = owner;
    work = run(owner).finally(() => { if (current === owner) work = undefined; });
    return work;
  }
  async function run(owner: AbortController): Promise<void> {
    preparing.value = !presented; failed.value = false; coreFailed.value = false;
    const advance = (done: number, total: number) => {
      if (current === owner && !owner.signal.aborted) progress(done, total);
    };
    frontend.progress = advance;
    try {
      stage.value = '准备界面'; progress(0, 0);
      await abortable(store.start(owner.signal), owner.signal);
      performance.mark('nexus.session.prepared');
    } catch (error) {
      if (owner.signal.aborted) return;
      preparing.value = false;
      if (!presented) { surfacesReady.value = false; coreFailed.value = true; }
      throw error;
    }
    let timer: ReturnType<typeof setTimeout> | undefined;
    const completedPages = new Set<string>();
    const foreground = async () => {
      stage.value = '加载插件界面'; progress(0, 0);
      await Promise.all([
        abortable(frontend.synchronize(), owner.signal).then(() => performance.mark('nexus.plugins.prepared')),
        preparePages(store, owner.signal, () => {}, report, true, completedPages).then(() => performance.mark('nexus.current-page.prepared'))
      ]);
      owner.signal.throwIfAborted();
      surfacesReady.value = true;
      registry.retryMounts();
      stage.value = '准备首次显示'; progress(0, 0);
      await nextTick();
      await registry.settled(owner.signal);
    };
    const pending = foreground().catch(error => { if (!owner.signal.aborted) report(error); });
    try {
      await Promise.race([pending, new Promise<void>(resolve => { timer = setTimeout(resolve, 5000); })]);
      if (owner.signal.aborted || current !== owner) return;
      surfacesReady.value = true;
      await nextTick();
      preparing.value = false;
      if (!presented) {
        presented = true;
        performance.mark('nexus.interface.presented');
        await desktop?.presented?.();
      }
      // Background reads and slow contributions retain their owner after presentation.
      await pending;
      await preparePages(store, owner.signal, () => {}, () => {}, false, completedPages);
      performance.mark('nexus.pages.prepared');
    } finally { clearTimeout(timer); }
  }
  function cancel(): void {
    current?.abort(); current = undefined; work = undefined;
    frontend.cancelPending();
  }
  return { preparing, surfacesReady, failed, coreFailed, stage, count, prepare, cancel, report };
}
