import type { Router, RouteLocationNormalized } from 'vue-router';
import { initPluginRuntime, resolvePluginRoute } from '@bridge/index';
import { enterPage, state } from '../platform/page-state';
import { navigationPreparation as preparation, type PreparedPage, type NavigationJob } from '../platform/navigation-preparation';

export interface PreparedPluginRoute {
  element: HTMLElement;
  controller: AbortController;
  dispose: () => void;
}

async function loadPage(to: RouteLocationNormalized, signal: AbortSignal): Promise<PreparedPage> {
  const page = to.path.split('/').filter(Boolean)[0];
  let data: unknown;
  switch (page) {
    case 'dashboard': data = await (await import('../features/dashboard/first-screen')).loadDashboardPage(signal); break;
    case 'scripts': data = await (await import('../features/scripts/first-screen')).loadScriptsPage(signal); break;
    case 'users': data = await (await import('../features/users/first-screen')).loadUsersPage(signal); break;
    case 'queues': data = await (await import('../features/queues/first-screen')).loadQueuesPage(signal); break;
    case 'dispatch': data = await (await import('../features/dispatch/first-screen')).loadDispatchPage(signal); break;
    case 'settings': data = await (await import('../features/settings/first-screen')).loadSettingsPage(signal); break;
    case 'plugins': data = await (await import('../features/plugins/first-screen')).loadPluginsPage(signal); break;
    case 'history': data = await (await import('../features/history/first-screen')).loadHistoryPage(signal, typeof to.query.recordId === 'string' ? to.query.recordId : undefined); break;
    case 'plugin': {
      await initPluginRuntime();
      const segments = to.path.split('/').filter(Boolean);
      const handler = resolvePluginRoute(segments);
      if (!handler) throw new Error('plugin_route_unavailable');
      const element = document.createElement('div');
      const controller = new AbortController();
      const abort = () => controller.abort();
      signal.addEventListener('abort', abort, { once: true });
      if (signal.aborted) abort();
      let cleanup: (() => void) | undefined;
      let disposed = false;
      const dispose = () => {
        if (disposed) return;
        disposed = true; controller.abort(); signal.removeEventListener('abort', abort);
        cleanup?.(); element.remove();
      };
      try {
        cleanup = await handler({ element, token: state.routeToken + 1, segments, signal: controller.signal });
        if (disposed || controller.signal.aborted) { dispose(); throw new DOMException('', 'AbortError'); }
        signal.removeEventListener('abort', abort);
        data = { element, controller, dispose } satisfies PreparedPluginRoute;
        return { data, dispose };
      } catch (error) { dispose(); throw error; }
    }
  }
  return { data };
}

export function installNavigationPreparation(router: Router) {
  const jobs = new WeakMap<object, NavigationJob>();
  const removeBegin = router.beforeEach(to => { jobs.set(to, preparation.begin(to.fullPath)); });
  const removeResolve = router.beforeResolve(async to => {
    const job = jobs.get(to);
    return job ? preparation.prepare(job, signal => loadPage(to, signal)) : false;
  });
  const removeCommit = router.afterEach((to, _from, failure) => {
    const job = jobs.get(to);
    if (!failure && job && preparation.commit(job)) enterPage(to.path.split('/').filter(Boolean)[0] || 'dashboard');
    else if ((job && preparation.current() === job) || (failure && to.fullPath === router.currentRoute.value.fullPath)) preparation.cancel();
  });
  const prepareInitial = async () => {
    const to = router.currentRoute.value;
    const job = preparation.begin(to.fullPath);
    if (await preparation.prepare(job, signal => loadPage(to, signal)) && preparation.commit(job)) {
      enterPage(to.path.split('/').filter(Boolean)[0] || 'dashboard');
    }
  };
  return { prepareInitial, dispose: () => { removeBegin(); removeResolve(); removeCommit(); preparation.cancel(); } };
}
