import { createApp, h, nextTick } from 'vue';
import QueueFields from '../../apps/web/src/pages/QueueFields.vue';
import PluginsPage from '../../apps/web/src/pages/PluginsPage.vue';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient } from '../../apps/web/src/control/client';
import type { AutomationBundle } from '../../apps/web/src/control/wire.generated';

export async function pageLoadingContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  let respond!: (value: Response) => void;
  const client = new ControlClient(location.origin, () => new Promise(resolve => { respond = resolve; }));
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
}
