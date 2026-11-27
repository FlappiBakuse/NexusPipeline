import { createApp, h, nextTick } from 'vue';
import DispatchPage from '../../apps/web/src/pages/DispatchPage.vue';
import FormSelect from '../../apps/web/src/components/FormSelect';
import uiTheme from '../../apps/web/src/ui/theme.css?inline';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient } from '../../apps/web/src/control/client';
import type { ActiveRun } from '../../apps/web/src/control/execution';
import type { UiElement } from '@nexuspipeline/extension-sdk';

export async function executionLayoutContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  await executionSubmissionContract(fixture, check);
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
    const kinds = mount.querySelector<UiElement<'nexus-select'>>('.target-controls label:first-child nexus-select')?.options;
    check(kinds?.map(item => item.label).join(',') === '脚本实例,调度队列', 'the run center only offers script and queue targets');
    const widths = () => [...mount.querySelectorAll('.dispatch-targets,.dispatch-running')].map(card => card.getBoundingClientRect().width);
    const idle = widths();
    const workspace = mount.querySelector<HTMLElement>('.dispatch-workspace')!;
    const emptyHeight = mount.querySelector('.dispatch-running')!.getBoundingClientRect().height;
    workspace.style.minHeight = '640px';
    check(emptyHeight > 0 && Math.abs(mount.querySelector('.dispatch-running')!.getBoundingClientRect().height - emptyHeight) < 1,
      'the idle card keeps its natural height when the desktop workspace grows');
    workspace.style.minHeight = '';
    const id = () => crypto.randomUUID();
    items = [{ operationId: id(), runId: id(), attemptId: id(), ordinal: 1, phase: 'running', canCancel: false,
      cancellationRequested: false, display: { targetKind: 'queue', targetId: id(), targetName: 'Queue', mode: 'manual', maximumAttempts: 3 },
      steps: [{ stepId: 'step', scriptId: id(), userId: null, bindingId: null, dependsOn: [], phase: 'running', logRoles: ['main'],
        invocationOrdinal: 1, maximumInvocations: 3, tasks: Array.from({ length: 10 }, (_, index) => ({
          taskId: 'task-' + index, title: 'Task ' + index, isSelected: true, isEnabled: true, dependencies: [], state: 'unknown'
        })) }] }];
    store.state.events = { executionRevision: 1 };
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
    const activity = mount.querySelector('.dispatch-running')!.getBoundingClientRect();
    check(Math.abs(activity.width - mount.getBoundingClientRect().width) < 1 &&
      !mount.querySelector('[role="tablist"], .dispatch-split'),
      'the single active run fills the workspace without a tab row');
    check(mount.querySelector('.run-steps li[aria-current="step"]')?.textContent?.includes('运行中'),
      'the queue strip identifies the active frozen step');
    for (const ordinal of [2, 3]) {
      const previousLog = mount.querySelector('.script-log-panel');
      items[0]!.steps[0]!.invocationOrdinal = ordinal;
      store.state.events = { executionRevision: ordinal };
      await settle();
      check(mount.querySelector('.current-step')?.textContent?.includes(`${ordinal}/3`),
        `the live subtitle includes invocation ${ordinal} within attempt one`);
      check(previousLog && mount.querySelector('.script-log-panel') !== previousLog,
        'a new invocation reopens all log subscriptions for the same step');
    }
    mount.style.width = '360px'; await settle();
    check(inside(), 'the running layout fits a phone-width content column');
    const scroll = mount.querySelector('.live-task-steps') as HTMLElement;
    const viewport = scroll?.shadowRoot?.querySelector('.viewport');
    check(viewport && viewport.scrollWidth > viewport.clientWidth, 'task overflow belongs to its horizontal scroll area');
    items[0]!.phase = 'reconciliationRequired';
    store.state.events = { executionRevision: 4 };
    await settle();
    check(!mount.querySelector('.dispatch-tab') &&
      mount.querySelectorAll('.dispatch-running .running-item').length === 1 && !!mount.querySelector('.dispatch-review'),
      'a quarantined run remains selectable and gets a pinned review notice');
    check(!mount.querySelector('.dispatch-running .running-item button.danger'), 'a quarantined run does not offer cancellation');
  } finally { app.unmount(); mount.remove(); store.dispose(); }
}

async function executionSubmissionContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  let items: ActiveRun[] = [];
  const requests: { id: string; resolve: (response: Response) => void }[] = [];
  const store = createStore(new ControlClient(location.origin, async input => {
    const path = new URL(String(input)).pathname;
    if (path.endsWith('/execution/active')) return Response.json({ items });
    if (path.includes('/execution/start/'))
      return new Promise<Response>(resolve => requests.push({ id: path.split('/').at(-1)!, resolve }));
    if (path.startsWith('/control/v1/records/')) return Response.json({ code: 'record_not_found' }, { status: 404 });
    throw Error('unexpected_execution_request:' + path);
  }));
  store.state.capabilities = { instanceId: crypto.randomUUID(), hostVersion: '0.18.0',
    capabilities: ['execution.start', 'execution.read', 'records.read'] };
  const queueId = crypto.randomUUID();
  store.state.automation = { catalogRevision: 1, roots: [], revisions: [],
    bundle: { format: 'nexus.automation.bundle', version: 1, exportKind: 'definitions', bundleId: crypto.randomUUID(),
      createdAt: new Date().toISOString(), roots: [], scripts: [], users: [], bindings: [], schedules: [], attachments: [],
      queues: [{ id: queueId, name: 'Queue', scriptIds: [], order: 0, completionAction: 'none',
        notifyEnabled: false, notificationTaskIds: [] }] } };
  const style = document.createElement('style');
  style.textContent = uiTheme;
  const mount = document.createElement('section');
  mount.style.cssText = 'display:grid;gap:16px;width:800px;max-width:100%';
  fixture.append(style, mount);
  const app = createApp({ render: () => h(DispatchPage, { store, visible: true }) });
  app.component('FormSelect', FormSelect).mount(mount);
  const settle = async () => { await nextTick(); await new Promise(resolve => setTimeout(resolve, 40)); await nextTick(); };
  const card = () => mount.querySelector('nexus-empty-state.running-empty');
  const title = () => card()?.shadowRoot?.querySelector('h3');
  const active = () => mount.querySelectorAll('.dispatch-running .running-item').length;
  const accept = (index: number, run: ActiveRun) => requests[index]!.resolve(Response.json({
    acceptance: { operationId: run.operationId, status: 'accepted' }, runId: run.runId, attemptId: run.attemptId
  }));
  const run = (index: number): ActiveRun => ({ operationId: requests[index]!.id, runId: crypto.randomUUID(),
    attemptId: crypto.randomUUID(), ordinal: 1, phase: 'running', canCancel: false, cancellationRequested: false, steps: [] });
  const select = (index: number) => mount.querySelectorAll('nexus-select')[index]!.dispatchEvent(
    new CustomEvent('nexus-change', { detail: { value: '1' } }));
  const submit = async () => {
    mount.querySelectorAll<HTMLElement>('.target-controls nexus-button')[1]!.click();
    await settle();
  };
  try {
    await settle(); select(0); await settle(); select(1); await settle();
    const idle = card();
    await submit();
    check(requests.length === 1 && card() === idle && title()?.textContent === '正在提交运行请求…' &&
      getComputedStyle(title()!).textAlign === 'center' && active() === 0,
      'the same centered empty card shows the outstanding start request');

    requests[0]!.resolve(Response.json({ code: 'fixture_rejected' }, { status: 409 }));
    await settle();
    check(card() === idle && title()?.textContent === '当前没有正在运行的任务' &&
      mount.querySelector('nexus-toast')?.shadowRoot?.textContent?.includes('fixture_rejected'),
      'a rejected start restores the original empty card and exposes the failure');

    await submit();
    const first = run(1);
    accept(1, first); await settle();
    check(card() === idle && title()?.textContent?.includes('运行请求已受理') && active() === 0,
      'the accepted request remains in the same card while waiting for the Host');
    items = [first]; store.state.events = { executionRevision: 1 };
    await settle();
    check(!card() && active() === 1, 'the active result replaces the empty state');

    await submit();
    check(active() === 1, 'the previous run stays visible during another submission');
    const second = run(2);
    items = [first, second]; store.state.events = { executionRevision: 2 };
    await settle();
    const tabs = [...mount.querySelectorAll<HTMLButtonElement>('.dispatch-tab')];
    check(active() === 1 && tabs.length === 2 && !mount.querySelector('.dispatch-workspace')?.textContent?.includes('正在提交运行请求'),
      'the matching active result arriving first replaces the submission feedback');
    accept(2, second); await settle();
    check(active() === 1 && !card(), 'the later response keeps one focused detail');
    const shown = () => [...mount.querySelectorAll('.running-item')].map(item => item.getAttribute('data-run-id')).join(',');
    const split = () => mount.querySelector<HTMLButtonElement>('.dispatch-split');
    tabs[0]!.focus();
    tabs[0]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    await settle();
    check(document.activeElement === tabs[1] && shown() === first.runId, 'arrows move tab focus without loading another run');
    tabs[1]!.click();
    await settle();
    check(shown() === second.runId && tabs[1]!.getAttribute('aria-selected') === 'true',
      'activating a tab mounts its matching detail and releases the previous pane');
    split()!.click();
    await settle();
    const panes = [...mount.querySelectorAll('.dispatch-running')].map(item => item.getBoundingClientRect());
    check(shown() === [second.runId, first.runId].join(',') && panes[0]!.right < panes[1]!.left,
      'split view opens exactly two different runs side by side');
    check(getComputedStyle(tabs[0]!).borderTopStyle === 'dashed' &&
      getComputedStyle(tabs[1]!).borderTopLeftRadius === '8px' &&
      getComputedStyle(tabs[1]!).borderBottomLeftRadius === '6px' &&
      tabs[0]!.querySelector('.tab-secondary-badge')?.textContent?.trim() === '右屏',
      'the prototype tab shape and right-pane marker distinguish both visible runs');
    tabs[0]!.click();
    await settle();
    check(shown() === [first.runId, second.runId].join(','), 'selecting the right tab moves the previous main run into comparison');
    mount.style.width = '360px';
    await settle();
    const tablist = mount.querySelector('.dispatch-tabs')!;
    check(shown() === first.runId && !split() && tablist.scrollWidth > tablist.clientWidth,
      'a phone-width view has one pane and a horizontally scrollable tab row');
    tabs[1]!.click();
    await settle();
    check(shown() === second.runId && tablist.scrollLeft > 0, 'selecting an overflowing tab reveals it without moving the page');
    items = [first]; store.state.events = { executionRevision: 3 };
    await settle();
    check(shown() === first.runId && !mount.querySelector('[role="tablist"]'),
      'finishing the focused run returns to a tab-free single detail');
  } finally { app.unmount(); mount.remove(); style.remove(); store.dispose(); }
}
