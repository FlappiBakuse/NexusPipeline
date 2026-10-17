import { shallowReactive } from 'vue';

export interface PreparedPage {
  data?: unknown;
  dispose?: () => void;
}
export interface NavigationJob {
  target: string;
  controller: AbortController;
  prepared?: PreparedPage;
  progressTimer: ReturnType<typeof setTimeout>;
  deadline: ReturnType<typeof setTimeout>;
  timedOut: boolean;
}

export function createNavigationPreparation() {
  const status = shallowReactive({ pending: false, progress: false, error: '', retryTarget: '', ready: false });
  let active: NavigationJob | undefined;
  let committed: { target: string; page: PreparedPage } | undefined;

  function clearTimers(job: NavigationJob) {
    clearTimeout(job.progressTimer);
    clearTimeout(job.deadline);
  }
  function cancel() {
    if (active) {
      clearTimers(active);
      active.controller.abort();
      active.prepared?.dispose?.();
      active = undefined;
    }
    status.pending = false;
    status.progress = false;
  }
  function begin(target: string): NavigationJob {
    cancel();
    committed?.page.dispose?.();
    committed = undefined;
    status.pending = true;
    status.error = '';
    status.retryTarget = '';
    const job: NavigationJob = {
      target, controller: new AbortController(), timedOut: false,
      progressTimer: setTimeout(() => { if (active === job) status.progress = true; }, 200),
      deadline: setTimeout(() => {
        if (active !== job) return;
        job.timedOut = true;
        status.error = 'timeout'; status.retryTarget = job.target; cancel();
      }, 30000),
    };
    active = job;
    return job;
  }
  async function prepare(job: NavigationJob, load: (signal: AbortSignal) => Promise<PreparedPage>): Promise<boolean> {
    if (active !== job || job.controller.signal.aborted) return false;
    const signal = job.controller.signal;
    let abort: () => void = () => {};
    const cancelled = new Promise<never>((_, reject) => {
      abort = () => reject(new DOMException('', 'AbortError'));
      signal.addEventListener('abort', abort, { once: true });
    });
    try {
      const loading = new Promise<PreparedPage>(resolve => resolve(load(signal))).then(page => {
        if (signal.aborted || active !== job) { page.dispose?.(); throw new DOMException('', 'AbortError'); }
        return page;
      });
      job.prepared = await Promise.race([loading, cancelled]);
      return active === job && !signal.aborted;
    } catch {
      if (active === job) {
        status.error = job.timedOut ? 'timeout' : 'failed';
        status.retryTarget = job.target;
        cancel();
      }
      return false;
    } finally { signal.removeEventListener('abort', abort); }
  }
  function commit(job: NavigationJob): boolean {
    if (active !== job || job.controller.signal.aborted || !job.prepared) return false;
    clearTimers(job);
    committed = { target: job.target, page: job.prepared };
    active = undefined;
    status.pending = false;
    status.progress = false;
    status.ready = true;
    return true;
  }
  function take<T>(page: string): T | undefined {
    if (!committed || committed.target.split(/[/?#]/).filter(Boolean)[0] !== page) return undefined;
    const result = committed.page.data as T;
    committed = undefined;
    return result;
  }
  return { status, begin, prepare, commit, cancel, take, current: () => active };
}

export const navigationPreparation = createNavigationPreparation();
export const takePreparedData = <T>(page: string) => navigationPreparation.take<T>(page);
