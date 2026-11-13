import { createApp, h, nextTick } from 'vue';
import DispatchPage from '../../apps/web/src/pages/DispatchPage.vue';
import FormSelect from '../../apps/web/src/components/FormSelect';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient } from '../../apps/web/src/control/client';
import type { ActiveRun } from '../../apps/web/src/control/execution';

export async function executionLayoutContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  let items: ActiveRun[] = [];
  const client = new ControlClient(location.origin, async () => Response.json({ items }));
  const store = createStore(client);
  store.logs.subscribe = () => () => {};
  const mount = document.createElement('section');
  mount.style.cssText = 'display:grid;gap:16px;width:800px;max-width:100%';
  fixture.append(mount);
  const app = createApp({ render: () => h(DispatchPage, { store, visible: true }) });
  app.component('FormSelect', FormSelect);
  app.mount(mount);
  const settle = async () => { await nextTick(); await new Promise(resolve => setTimeout(resolve, 40)); await nextTick(); };
  try {
    await settle();
    const widths = () => [...mount.querySelectorAll('.dispatch-targets,.dispatch-running')].map(card => card.getBoundingClientRect().width);
    const idle = widths();
    const id = () => crypto.randomUUID();
    items = [{ operationId: id(), runId: id(), attemptId: id(), ordinal: 1, phase: 'running', canCancel: false,
      cancellationRequested: false, display: { targetKind: 'queue', targetId: id(), targetName: 'Queue', mode: 'manual', maximumAttempts: 3 },
      steps: [{ stepId: 'step', scriptId: id(), userId: null, bindingId: null, dependsOn: [], phase: 'running', logRoles: ['main'],
        invocationOrdinal: 1, maximumInvocations: 3, tasks: Array.from({ length: 10 }, (_, index) => ({
          taskId: 'task-' + index, title: 'Task ' + index, isSelected: true, isEnabled: true, dependencies: [], state: 'unknown'
        })) }] }];
    store.state.revision++;
    await settle();
    check(mount.querySelectorAll('.task-step').length === 10 || mount.querySelectorAll('.task-list li').length === 10 ||
      mount.querySelectorAll('.live-task-steps li').length === 10, 'running layout renders ten actual task cards');
    check(widths().every((width, index) => Math.abs(width - idle[index]!) < 1),
      'starting a ten-task run preserves both desktop card widths');
    const inside = () => {
      const bounds = mount.getBoundingClientRect();
      return [...mount.querySelectorAll('.dispatch-targets,.dispatch-running')].every(card => {
        const rect = card.getBoundingClientRect(); return rect.left >= bounds.left - 1 && rect.right <= bounds.right + 1;
      });
    };
    check(inside(), 'running cards remain inside the desktop page');
    for (const ordinal of [2, 3]) {
      items[0]!.steps[0]!.invocationOrdinal = ordinal;
      store.state.revision++;
      await settle();
      check(mount.querySelector('.current-step')?.textContent?.includes(`${ordinal}/3`),
        `the live subtitle includes invocation ${ordinal} within attempt one`);
    }
    mount.style.width = '500px'; await settle();
    check(inside(), 'the running layout also fits a narrower desktop content column');
    const scroll = mount.querySelector('nexus-scroll-area') as HTMLElement;
    const viewport = scroll?.shadowRoot?.querySelector('.viewport');
    check(viewport && viewport.scrollWidth > viewport.clientWidth, 'task overflow belongs to its horizontal scroll area');
  } finally { app.unmount(); mount.remove(); store.dispose(); }
}
