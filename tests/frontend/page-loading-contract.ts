import { createApp, h, nextTick } from 'vue';
import QueueFields from '../../apps/web/src/pages/QueueFields.vue';
import PluginsPage from '../../apps/web/src/pages/PluginsPage.vue';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient } from '../../apps/web/src/control/client';
import type { AutomationBundle, ControlHostSettingsValues } from '../../apps/web/src/control/wire.generated';
import { t } from '../../apps/web/src/app/locale';

export async function pageLoadingContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  let respond!: (value: Response) => void;
  const paths: string[] = [];
  const client = new ControlClient(location.origin, input => new Promise(resolve => {
    paths.push(new URL(String(input)).pathname); respond = resolve;
  }));
  const store = createStore(client);
  store.state.capabilities = { instanceId: 'page-fixture', hostVersion: '0.18.0', capabilities: [] };
  const mount = document.createElement('section'); fixture.append(mount);
  const plugins = createApp({ render: () => h(PluginsPage, { store, visible: true }) });
  plugins.mount(mount);
  try {
    await nextTick();
    check(mount.querySelector('nexus-loading-state')?.shadowRoot?.querySelector('[role=progressbar]'),
      'plugin center shows the shared progress animation while its first catalog is pending');
    check(!mount.querySelector('.plugin-list nexus-empty-state'), 'pending plugins are not reported as an empty search');
    respond(Response.json({ hasReleaseTrust: false, items: [] }));
    for (let i = 0; i < 50 && mount.querySelector('nexus-loading-state'); i++)
      await new Promise(resolve => setTimeout(resolve, 10));
    check(!mount.querySelector('nexus-loading-state') && mount.querySelector('.plugin-list nexus-empty-state'),
      'a completed empty plugin list replaces the progress animation');
    store.state.preparationRevision++;
    await nextTick();
    check(mount.querySelector('nexus-loading-state'),
      'preparation retry refreshes an already visible page without navigation');
    respond(Response.json({ hasReleaseTrust: false, items: [] }));
    for (let i = 0; i < 50 && mount.querySelector('nexus-loading-state'); i++)
      await new Promise(resolve => setTimeout(resolve, 10));
    check(!mount.querySelector('nexus-loading-state'), 'the visible page consumes the recovered readonly snapshot');
    store.state.capabilities.capabilities = ['extensions.read', 'extensions.write'];
    store.state.preparationRevision++;
    await nextTick();
    respond(Response.json({ hasReleaseTrust: false, items: [{
      extensionId: 'damaged-plugin', installedRevision: 1, isInstalled: true, desiredEnabled: false,
      activationId: null, unavailableReason: 'extension_files_changed', packageVersion: '0.5.0',
      kind: 'managed-code', artifactName: 'DamagedPlugin', capabilities: [], metadata: null
    }] }));
    for (let i = 0; i < 50 && !mount.querySelector('.plugin-details'); i++)
      await new Promise(resolve => setTimeout(resolve, 10));
    respond(Response.json({ code: 'extension_manifest_missing' }, { status: 503 }));
    const remove = () => [...mount.querySelectorAll<HTMLButtonElement>('button')]
      .find(button => button.textContent?.trim() === t('卸载插件'));
    const detailError = () => mount.querySelector('.plugin-details nexus-toast[role=alert]:popover-open');
    for (let i = 0; i < 50 && (!detailError() || remove()?.disabled); i++)
      await new Promise(resolve => setTimeout(resolve, 10));
    check(detailError() && remove() && !remove()!.disabled,
      'a damaged local plugin remains removable when its installed documents are unavailable');
    remove()!.click();
    await nextTick();
    const confirmation = document.querySelector('nexus-confirm-dialog');
    check(confirmation, 'removing a damaged local plugin retains the business confirmation');
    confirmation?.dispatchEvent(new CustomEvent('nexus-cancel'));
    for (let i = 0; i < 50 && document.querySelector('nexus-confirm-dialog'); i++)
      await new Promise(resolve => setTimeout(resolve, 10));
    check(!paths.some(path => path.includes('/catalog') || path.endsWith('/apply')),
      'a cancelled local removal requires no catalog access and sends no uninstall command');
  } finally { plugins.unmount(); }
  const bundle = { scripts: [], schedules: [], users: [], bindings: [], queues: [], attachments: [], roots: [] } as unknown as AutomationBundle;
  const schedule = { id: 'schedule', queueId: 'queue', weekdays: [], localMinute: 330, enabled: true, order: 0 };
  let changed: readonly { weekdays: readonly number[] }[] = [];
  const queue = createApp({ render: () => h(QueueFields, { store, bundle,
    modelValue: { id: 'queue', name: '', scriptIds: [], notificationTaskIds: [], runOnStartup: false, scheduleEnabled: true } as never,
    schedules: [schedule], 'onUpdate:schedules': value => { changed = value; }
  }) });
  queue.mount(mount);
  try {
    await nextTick();
    const days = [...mount.querySelectorAll<HTMLButtonElement>('.weekdays button')];
    check(days.length === 7 && days.every(day => !day.labels?.length),
      'weekday controls have a group label without associating every hover with Sunday');
    days[2]!.click();
    check(changed[0]?.weekdays.join(',') === '2', 'choosing Tuesday changes only Tuesday');
  } finally { queue.unmount(); mount.remove(); store.dispose(); }
  await pluginEnable(fixture, check);
  await queueNotificationChoices(fixture, check);
}

async function pluginEnable(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  let revision = 1;
  let values: ControlHostSettingsValues = { autoStart: false, desktop: { mode: 'browser', minimizeToTray: true, openOnStartup: false },
    service: { webPort: 0, allowRemoteAccess: false, mcpEnabled: false, mcpWriteEnabled: false, mcpPort: 18291 },
    proxy: { mode: 'none', url: null, userName: '' }, allowConfigRepair: false, historyRetentionDays: 30, logLevel: 'info', locale: 'zhCn',
    extensions: [{ extensionId: 'switchable', enabled: true }], updates: { checkEnabled: false, autoApplyEnabled: false,
      pluginAutoUpdateEnabled: false, channel: 'stable', sourceUrl: null, pluginSource: 'develop' }, automaticRetry: { enabled: false, backoffSeconds: 5 } };
  const item = { extensionId: 'switchable', installedRevision: 1, isInstalled: true, desiredEnabled: true,
    activationId: 'fixture-activation' as string | null, unavailableReason: null, capabilities: [] };
  const requests: string[] = [];
  const catalogRequested = Promise.withResolvers<void>(), releaseCatalog = Promise.withResolvers<void>();
  let loseReconcile = false, catalogSignal: AbortSignal | undefined;
  const runtime = () => ({ saved: { revision, values }, effective: values, restartReasons: [] });
  const store = createStore(new ControlClient(location.origin, async (input, init) => {
    const path = new URL(String(input)).pathname;
    requests.push(`${init?.method || 'GET'} ${path}`);
    if (path === '/control/v1/extensions') return Response.json({ hasReleaseTrust: loseReconcile, items: [item] });
    if (path === '/control/v1/extensions/operations') return Response.json({ items: [], nextOffset: null });
    if (path === '/control/v1/extensions/catalog/develop') {
      catalogSignal = init?.signal ?? undefined;
      catalogRequested.resolve();
      await releaseCatalog.promise;
      return Response.json({ identity: { channel: 'develop', generationId: 'release-fixture', sequence: 1, sha256: 'a'.repeat(64) },
        publishedAt: '2026-01-01T00:00:00Z', entries: [{ extensionId: 'repository-candidate', artifactName: 'Repository Candidate',
          packageVersion: '0.18.0', kind: 'managed-code', minHostVersion: '0.18.0', sdkVersion: '0.18.0', artifactBytes: 1,
          artifactUrl: 'https://example.invalid/plugin.zip', artifactSha256: 'b'.repeat(64), manifestSha256: 'c'.repeat(64) }] });
    }
    if (path === '/control/v1/extensions/reconcile') {
      const request = JSON.parse(String(init?.body));
      if (request.expectedSettingsRevision !== revision || request.expectedInstalledRevision !== item.installedRevision)
        throw new Error('The plugin command lost its revision binding');
      item.desiredEnabled = values.extensions[0]!.enabled;
      item.activationId = item.desiredEnabled ? 'fixture-activation' : null;
      if (loseReconcile) throw new TypeError('fixture lost the response after reconciliation');
      return Response.json(item);
    }
    if (path === '/control/v1/settings') {
      if (init?.method !== 'POST') return Response.json(runtime());
      const request = JSON.parse(String(init.body));
      if (request.expectedRevision !== revision) throw new Error('The preference used an unexpected revision');
      values = request.values; revision++;
      return Response.json({ operationId: request.operationId, saved: runtime().saved });
    }
    return Response.json({ code: 'fixture_documents_unavailable' }, { status: 503 });
  }));
  store.state.capabilities = { instanceId: 'plugin-enable-fixture', hostVersion: '0.18.0',
    capabilities: ['extensions.read', 'extensions.write', 'operations.read', 'settings.write'] };
  store.state.settings = runtime();
  const holder = document.createElement('section'); fixture.append(holder);
  const app = createApp({ render: () => h(PluginsPage, { store, visible: true }) });
  app.mount(holder);
  const button = (label: string) => [...holder.querySelectorAll('button')].find(item => item.textContent?.trim() === t(label) && !item.disabled);
  const wait = async (condition: () => unknown) => {
    for (let index = 0; index < 100 && !condition(); index++) await new Promise(resolve => setTimeout(resolve, 10));
  };
  try {
    await wait(() => button('禁用插件'));
    check(button('禁用插件'), 'the installed plugin offers disable before applying its preference');
    requests.length = 0;
    button('禁用插件')!.click();
    await wait(() => button('启用插件'));
    check(button('启用插件') && item.activationId === null && store.state.settings?.saved.revision === 2,
      'the visible plugin page adopts the verified disabled directory');
    check(requests.filter(value => value === 'GET /control/v1/extensions').length === 1 &&
      requests.filter(value => value === 'GET /control/v1/settings').length === 1 &&
      requests.filter(value => value.startsWith('POST')).length === 2,
      'a successful plugin toggle uses one settings and one installed-directory GET for both owner and page');
    loseReconcile = true;
    button('启用插件')!.click();
    await catalogRequested.promise;
    await wait(() => button('读取操作结果'));
    check(button('读取操作结果'), 'the original operation can be inspected while repository reading is active');
    button('读取操作结果')!.click();
    await wait(() => !holder.textContent?.includes(t('读取操作结果')));
    check(catalogSignal && !catalogSignal.aborted, 'adopting the verified directory preserves the live repository request');
    releaseCatalog.resolve();
    await wait(() => button('插件仓库'));
    button('插件仓库')!.click();
    await wait(() => holder.textContent?.includes('Repository Candidate'));
    check(holder.textContent?.includes('Repository Candidate'), 'the repository result remains visible after resolving the original plugin operation');
  } finally { releaseCatalog.resolve(); app.unmount(); store.dispose(); holder.remove(); }
}

async function queueNotificationChoices(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  let reads = 0;
  const release = Promise.withResolvers<void>();
  const store = createStore(new ControlClient(location.origin, async input => {
    if (new URL(String(input)).pathname !== '/control/v1/notifications') throw new Error('Unexpected editor request');
    reads++;
    await release.promise;
    return Response.json({ revision: 1, maximumTasks: 20, tasks: [{ id: 'saved-notification', name: 'Saved notification',
      revision: 1, remark: '', kind: 'smtp', isEnabled: true, configuration: {} }] });
  }));
  store.state.capabilities = { instanceId: 'choices-fixture', hostVersion: '0.18.0', capabilities: ['notifications.read'] };
  store.state.events = { mutationSequence: 1 };
  const holder = document.createElement('section'); fixture.append(holder);
  const bundle = { scripts: [], schedules: [], users: [], bindings: [], queues: [], attachments: [], roots: [] } as unknown as AutomationBundle;
  const app = createApp({ render: () => h('div', [0, 1, 2].map(id => h(QueueFields, { store, bundle,
    modelValue: { id: 'queue-' + id, name: '', scriptIds: [], notificationTaskIds: [], runOnStartup: false } as never, schedules: [] }))) });
  app.mount(holder);
  const options = () => Array.from(holder.querySelectorAll<HTMLElement & { options: { id: string }[] }>('nexus-select[data-draft-field$="notificationTaskIds"]'));
  try {
    await nextTick();
    release.resolve();
    for (let index = 0; index < 50 && options().some(item => !item.options?.length); index++)
      await new Promise(resolve => setTimeout(resolve, 10));
    check(options().length === 3 && options().every(item => item.options[0]?.id === 'saved-notification'),
      'each active queue reads the same current notification choices');
    check(reads === 1, `three simultaneous queue editors share one notification directory GET (${reads})`);
  } finally { release.resolve(); app.unmount(); store.dispose(); holder.remove(); }
}
