import { afterEach, describe, expect, it, vi } from 'vitest';
import { createNavigationPreparation } from './navigation-preparation';
import { createMemoryHistory, createRouter } from 'vue-router';
import { installNavigationPreparation } from '../app/navigation';
import { createNexusRouter } from '../router';
import { syncPluginNavActive, resolvePluginRoute } from '@bridge/index';
vi.mock('@bridge/index', () => ({ syncPluginNavActive: vi.fn(), initPluginRuntime: vi.fn(async () => {}), resolvePluginRoute: vi.fn() }));

afterEach(() => vi.useRealTimers());
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

describe('navigation preparation', () => {
  it('keeps plugin navigation on the committed route after a rejected navigation', async () => {
    const router = createNexusRouter(createMemoryHistory());
    await router.push('/unknown');
    expect(syncPluginNavActive).toHaveBeenLastCalledWith('#/unknown');
    vi.mocked(syncPluginNavActive).mockClear();
    router.beforeEach(() => false);
    await router.push('/plugin/example/tasks');
    expect(router.currentRoute.value.fullPath).toBe('/unknown');
    expect(syncPluginNavActive).not.toHaveBeenCalled();
  });
  it('commits fresh data once without loading it again', async () => {
    const navigation = createNavigationPreparation();
    const load = vi.fn(async () => ({ data: { revision: 7 } }));
    const job = navigation.begin('/scripts');
    expect(await navigation.prepare(job, load)).toBe(true);
    expect(navigation.status.ready).toBe(false);
    expect(navigation.commit(job)).toBe(true);
    expect(navigation.take('users')).toBeUndefined();
    expect(navigation.take('scripts')).toEqual({ revision: 7 });
    expect(navigation.take('scripts')).toBeUndefined();
    expect(load).toHaveBeenCalledTimes(1);
  });
  it('aborts superseded work and rejects its late result', async () => {
    const navigation = createNavigationPreparation();
    const old = deferred<{ data: string; dispose: () => void }>();
    const cleanup = vi.fn();
    const first = navigation.begin('/scripts');
    const pending = navigation.prepare(first, () => old.promise);
    const last = navigation.begin('/users');
    expect(first.controller.signal.aborted).toBe(true);
    expect(await navigation.prepare(last, async () => ({ data: 'latest' }))).toBe(true);
    expect(navigation.commit(last)).toBe(true);
    expect(await pending).toBe(false);
    old.resolve({ data: 'obsolete', dispose: cleanup });
    await Promise.resolve(); await Promise.resolve();
    expect(cleanup).toHaveBeenCalledTimes(1);
    expect(navigation.commit(first)).toBe(false);
    expect(navigation.take('users')).toBe('latest');
    expect(navigation.status.error).toBe('');
  });
  it('releases a prepared plugin when navigation is cancelled', async () => {
    const navigation = createNavigationPreparation();
    const cleanup = vi.fn();
    const job = navigation.begin('/plugin/example/tasks');
    await navigation.prepare(job, async () => ({ data: {}, dispose: cleanup }));
    navigation.cancel(); navigation.cancel();
    expect(job.controller.signal.aborted).toBe(true);
    expect(cleanup).toHaveBeenCalledTimes(1);
    expect(navigation.status.pending).toBe(false);

    const handlerResult = deferred<() => void>();
    const handler = vi.fn(() => handlerResult.promise);
    vi.mocked(resolvePluginRoute).mockReturnValue(handler);
    const router = createRouter({ history: createMemoryHistory(), routes: [
      { path: '/plugin/:name/:page', component: { render: () => null } },
      { path: '/unknown', component: { render: () => null } },
    ] });
    const hooks = installNavigationPreparation(router);
    const pendingRoute = router.push('/plugin/example/tasks');
    await vi.waitFor(() => expect(handler).toHaveBeenCalledTimes(1));
    await router.push('/unknown');
    const lateCleanup = vi.fn();
    handlerResult.resolve(lateCleanup);
    await pendingRoute;
    await vi.waitFor(() => expect(lateCleanup).toHaveBeenCalledTimes(1));
    expect(router.currentRoute.value.path).toBe('/unknown');
    hooks.dispose();

  });
  it('transfers plugin cleanup to the committed page owner', async () => {
    const navigation = createNavigationPreparation();
    const cleanup = vi.fn();
    const job = navigation.begin('/plugin/example/tasks');
    await navigation.prepare(job, async () => ({ data: { dispose: cleanup }, dispose: cleanup }));
    navigation.commit(job);
    const page = navigation.take<{ dispose: () => void }>('plugin')!;
    const next = navigation.begin('/scripts');
    expect(cleanup).not.toHaveBeenCalled();
    page.dispose(); navigation.cancel();
    expect(cleanup).toHaveBeenCalledTimes(1);
    expect(next.controller.signal.aborted).toBe(true);
  });
  it('retains a retry target after failure and fetches new data on retry', async () => {
    const navigation = createNavigationPreparation();
    const job = navigation.begin('/settings');
    expect(await navigation.prepare(job, async () => { throw new Error('unavailable'); })).toBe(false);
    expect(navigation.status).toMatchObject({ error: 'failed', retryTarget: '/settings', pending: false, ready: false });
    const immediate = navigation.begin(navigation.status.retryTarget);
    expect(await navigation.prepare(immediate, () => { throw new Error('synchronous failure'); })).toBe(false);
    expect(navigation.status).toMatchObject({ error: 'failed', retryTarget: '/settings', pending: false });
    const retry = navigation.begin(navigation.status.retryTarget);
    expect(await navigation.prepare(retry, async () => ({ data: 'recovered' }))).toBe(true);
    expect(navigation.commit(retry)).toBe(true);
    expect(navigation.take('settings')).toBe('recovered');
    expect(navigation.status.error).toBe('');
  });
  it('times out unresponsive work and cleans up a late plugin', async () => {
    vi.useFakeTimers();
    const navigation = createNavigationPreparation();
    const old = deferred<{ dispose: () => void }>();
    const cleanup = vi.fn();
    const job = navigation.begin('/plugin/example/tasks');
    const pending = navigation.prepare(job, () => old.promise);
    await vi.advanceTimersByTimeAsync(30000);
    expect(await pending).toBe(false);
    expect(navigation.status).toMatchObject({ error: 'timeout', pending: false, retryTarget: job.target });
    expect(job.controller.signal.aborted).toBe(true);
    old.resolve({ dispose: cleanup }); await vi.advanceTimersByTimeAsync(0);
    expect(cleanup).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);
  });
  it('cancels the deadline even when component loading has not reached preparation', async () => {
    vi.useFakeTimers();
    const navigation = createNavigationPreparation();
    const job = navigation.begin('/history?recordId=example');
    await vi.advanceTimersByTimeAsync(30000);
    const load = vi.fn();
    expect(await navigation.prepare(job, load)).toBe(false);
    expect(load).not.toHaveBeenCalled();
    expect(navigation.status).toMatchObject({ error: 'timeout', pending: false });
    expect(vi.getTimerCount()).toBe(0);
  });
});
