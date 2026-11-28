import { createApp, h, ref } from 'vue';
import { AutomationEditor, automationSession } from '../../apps/web/src/pages/automation-editor';
import AutomationEditorDialog from '../../apps/web/src/pages/AutomationEditorDialog.vue';
import theme from '../../apps/web/src/styles/theme.css?inline';
import workspace from '../../apps/web/src/styles/workspace.css?inline';
import { draftFixture, settle } from './configuration-draft-fixture';
import QueueTasks from '../../apps/web/src/pages/QueueTasks.vue';
import type { AutomationBundle } from '../../apps/web/src/control/wire.generated';

export async function queueDraftItemsContract(parent: HTMLElement, check: (value: unknown, name: string) => void) {
  const { store } = draftFixture(), session = automationSession(store, 'queues');
  const model = new AutomationEditor(store, () => 'queues', session);
  model.edit();
  const saved = { ...model.draft.value, name: 'Saved queue', scheduleEnabled: true };
  model.discard();
  store.state.automation = { ...store.state.automation!, bundle: { ...store.state.automation!.bundle, queues: [saved as never] } };
  const holder = document.createElement('section'); parent.append(holder);
  const style = document.createElement('style'); style.textContent = [theme, workspace].join('\n'); document.head.append(style);
  const originalTheme = document.body.dataset.theme;
  const app = createApp({ render: () => model.editing.value ? h(AutomationEditorDialog,
    { store, kind: 'queues', visible: true, model, session, onClose: () => { model.editing.value = false; } }) : null });
  app.mount(holder);
  const click = (caption: string) => [...holder.querySelectorAll('button')].find(button => button.textContent?.includes(caption))!.click();
  const removeSchedule = () => {
    const control = holder.querySelector<HTMLElement & { icon: string }>('.end-actions nexus-icon-button');
    const button = control?.shadowRoot?.querySelector('button');
    check(control?.icon === 'trash' && button?.getAttribute('aria-label') === '删除定时' &&
      !!button.querySelector('svg path') && !button.textContent?.trim() && button.type === 'button',
      'the schedule uses the labelled shared SVG removal action');
    button!.click();
  };
  const fill = () => getComputedStyle(holder.querySelector('.schedule-item')!).backgroundColor;
  const scheduleTrigger = () => holder.querySelector<HTMLButtonElement>('.schedule-heading button')!;
  try {
    model.edit(saved); await settle();
    const key = model.draftKey.value;
    click('添加定时'); await settle();
    document.body.dataset.theme = 'dark';
    check(fill() === 'rgba(0, 0, 0, 0)', 'a new expanded schedule uses the dialog surface without a highlight');
    check(scheduleTrigger().textContent?.includes('点击收回') && !scheduleTrigger().querySelector('nexus-icon, .chevron'),
      'an expanded schedule uses the requested text instead of an arrow');
    holder.querySelector('nexus-time-picker')!.dispatchEvent(new CustomEvent('nexus-change',
      { detail: { value: '10:00' }, bubbles: true, composed: true })); await settle();
    check(model.queueSchedules.value[0].localMinute === 600, 'the real queue editor receives the new schedule time');
    removeSchedule(); await settle();
    check(!model.edited && model.queueSchedules.value.length === 0, 'removing the new queue schedule restores the saved model');
    holder.querySelector('nexus-modal')!.shadowRoot!.querySelector<HTMLButtonElement>('button[aria-label="关闭"],button[aria-label="Close"]')!.click();
    await settle();
    check(!store.drafts.has(key), 'removing an edited new schedule releases a pristine queue draft on close');
    model.edit(saved); await settle();
    check(!holder.querySelector('nexus-toast.draft-restored-toast'), 'reopening the unchanged queue shows no restored draft after discarding a new schedule');
    model.editing.value = false; await settle();
    const original = { id: 'original-schedule', queueId: saved.id, enabled: true, weekdays: [1],
      localMinute: 540, timeZoneId: 'Asia/Tokyo', missedPolicy: 'skip' as const, order: 0 };
    store.state.automation = { ...store.state.automation!, bundle: { ...store.state.automation!.bundle, schedules: [original as never] } };
    model.edit(saved); await settle();
    document.body.dataset.theme = 'light';
    check(fill() === 'rgba(0, 0, 0, 0)', 'a saved collapsed schedule uses the same neutral surface in light mode');
    check(scheduleTrigger().textContent?.includes('点击展开'), 'a collapsed schedule offers the expand caption');
    holder.querySelector<HTMLButtonElement>('.schedule-heading button')!.click(); await settle();
    check(fill() === 'rgba(0, 0, 0, 0)', 'expanding a saved schedule does not tint its card');
    holder.querySelector('nexus-time-picker')!.dispatchEvent(new CustomEvent('nexus-change',
      { detail: { value: '10:00' }, bubbles: true, composed: true })); await settle();
    holder.querySelector<HTMLButtonElement>('.schedule-heading button')!.click(); await settle();
    check(!holder.querySelector('nexus-time-picker'), 'collapsing an existing queue schedule removes its visible time field');
    model.editing.value = false; await settle(); model.edit(saved); await settle();
    check(!!holder.querySelector('nexus-toast.draft-restored-toast'), 'a collapsed existing queue schedule keeps its edited draft on reopen');
    holder.querySelector<HTMLButtonElement>('.schedule-heading button')!.click(); await settle();
    check(holder.querySelector('nexus-time-picker')!.hasAttribute('data-unsaved'), 'recreated queue time fields retain the saved baseline');
    removeSchedule(); await settle();
    model.queueSchedules.value = [original as never]; await settle();
    holder.querySelector<HTMLButtonElement>('.schedule-heading button')!.click(); await settle();
    check(!model.edited && !holder.querySelector('nexus-time-picker')?.hasAttribute('data-unsaved'),
      'undoing an existing schedule deletion restores its business and field baselines');
    model.editing.value = false; await settle();
    model.edit(); await settle();
    check(!model.existing.value && model.queueSchedules.value.length === 1, 'a new queue opens with its default schedule');
    click('添加任务'); await settle();
    const task = holder.querySelector('.task-row')!;
    const control = task.querySelector<HTMLElement & { icon: string }>('nexus-icon-button');
    const action = control?.shadowRoot?.querySelector('button');
    const right = (element: Element) => element.getBoundingClientRect().right;
    const modal = holder.querySelector('nexus-modal')!;
    const shadow = modal.shadowRoot!;
    check(Math.abs(right(task.querySelector('nexus-icon-button, button')!) - right(holder.querySelector('.end-actions nexus-icon-button')!)) < 0.5 &&
      Math.abs(right(shadow.querySelector('.close')!) - right(holder.querySelector('.editor-actions')!)) < 0.5,
      'queue task, schedule, and modal actions share the same trailing edge');
    check(control?.icon === 'trash' && action?.getAttribute('aria-label') === '删除任务' &&
      !!action.querySelector('svg path') && action.type === 'button', 'queue tasks use the labelled shared SVG removal action');
    action!.click(); await settle();
    check(!holder.querySelector('.task-row') && !(model.draft.value.scriptIds as string[]).length,
      'the task removal action updates the queue draft');
    removeSchedule(); await settle();
    check(!model.queueSchedules.value.length, 'the shared remove action also removes a schedule in a new queue');
  } finally {
    app.unmount(); holder.remove(); store.dispose(); style.remove();
    if (originalTheme === undefined) delete document.body.dataset.theme;
    else document.body.dataset.theme = originalTheme;
  }
  await indexedQueueItems(parent, check);
}

async function indexedQueueItems(parent: HTMLElement, check: (value: unknown, name: string) => void) {
  const holder = document.createElement('section'); parent.append(holder);
  type Queue = AutomationBundle['queues'][number];
  const queue = ref<Queue>({ id: 'indexed-queue', name: 'Indexed queue', order: 0, completionAction: 'none',
    notifyEnabled: false, notificationTaskIds: [], scriptIds: Array.from({ length: 12 }, (_, index) => 'script-' + index),
    successDependencies: [{ scriptId: 'script-1', requiredScriptIds: ['script-0'] }] });
  let labelReads = 0, updates = 0;
  const scripts = Array.from({ length: 48 }, (_, index) => ({ id: 'script-' + index,
    get name() { labelReads++; return 'Script ' + index; } })) as AutomationBundle['scripts'];
  const app = createApp({ render: () => h(QueueTasks, { scripts, modelValue: queue.value,
    'onUpdate:modelValue': value => { queue.value = value; updates++; } }) });
  app.mount(holder);
  const ids = () => Array.from(holder.querySelectorAll<HTMLElement>('.task-row')).map(row => row.dataset.itemId!);
  const reorder = (next: string[]) => holder.querySelector('nexus-sortable-list')!.dispatchEvent(new CustomEvent('nexus-reorder', { detail: { ids: next } }));
  try {
    await settle();
    check(ids().length === 12, 'the queue renders one task control for each declared script');
    check(labelReads === 48, `all task controls in one queue share the computed script options (${labelReads} label reads)`);
    const original = ids();
    reorder([original[0]!, ...original.slice(0, -1)]);
    await settle();
    check(!updates && ids().join(',') === original.join(','), 'a repeated task identity cannot reorder the queue');
    const moved = [original[1]!, original[0]!, ...original.slice(2)];
    reorder(moved);
    await settle();
    check(ids().join(',') === moved.join(',') && queue.value.scriptIds[0] === 'script-1' &&
      !queue.value.successDependencies?.[0]?.requiredScriptIds.length, 'queue reorder preserves row identity and removes a now-later prerequisite');
    queue.value = { ...queue.value, scriptIds: ['script-0', ...queue.value.scriptIds, 'script-0'] };
    await settle();
    const withDuplicates = ids();
    check(withDuplicates[0] === original[0] && withDuplicates[1] === original[1] &&
      new Set(withDuplicates).size === withDuplicates.length, 'external updates retain the first matching task and allocate independent duplicate drafts');
  } finally { app.unmount(); holder.remove(); }
}
