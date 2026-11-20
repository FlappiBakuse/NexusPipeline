import { createApp, h } from 'vue';
import { AutomationEditor } from '../../apps/web/src/pages/automation-editor';
import { automationSession } from '../../apps/web/src/pages/automation-session';
import AutomationEditorDialog from '../../apps/web/src/pages/AutomationEditorDialog.vue';
import { draftFixture, settle } from './configuration-draft-fixture';

export async function queueDraftItemsContract(parent: HTMLElement, check: (value: unknown, name: string) => void) {
  const { store } = draftFixture(), session = automationSession(store, 'queues');
  const model = new AutomationEditor(store, () => 'queues', session);
  model.edit();
  const saved = { ...model.draft.value, name: 'Saved queue', scheduleEnabled: true };
  model.discard();
  store.state.automation = { ...store.state.automation!, bundle: { ...store.state.automation!.bundle, queues: [saved as never] } };
  const holder = document.createElement('section'); parent.append(holder);
  const app = createApp({ render: () => model.editing.value ? h(AutomationEditorDialog,
    { store, kind: 'queues', visible: true, model, session, onClose: () => { model.editing.value = false; } }) : null });
  app.mount(holder);
  const click = (caption: string) => [...holder.querySelectorAll('button')].find(button => button.textContent?.includes(caption))!.click();
  try {
    model.edit(saved); await settle();
    const key = model.draftKey.value;
    click('添加定时'); await settle();
    holder.querySelector('nexus-time-picker')!.dispatchEvent(new CustomEvent('nexus-change',
      { detail: { value: '10:00' }, bubbles: true, composed: true })); await settle();
    check(model.queueSchedules.value[0].localMinute === 600, 'the real queue editor receives the new schedule time');
    click('删除定时'); await settle();
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
    holder.querySelector<HTMLButtonElement>('.schedule-heading button')!.click(); await settle();
    holder.querySelector('nexus-time-picker')!.dispatchEvent(new CustomEvent('nexus-change',
      { detail: { value: '10:00' }, bubbles: true, composed: true })); await settle();
    holder.querySelector<HTMLButtonElement>('.schedule-heading button')!.click(); await settle();
    check(!holder.querySelector('nexus-time-picker'), 'collapsing an existing queue schedule removes its visible time field');
    model.editing.value = false; await settle(); model.edit(saved); await settle();
    check(!!holder.querySelector('nexus-toast.draft-restored-toast'), 'a collapsed existing queue schedule keeps its edited draft on reopen');
    holder.querySelector<HTMLButtonElement>('.schedule-heading button')!.click(); await settle();
    check(holder.querySelector('nexus-time-picker')!.hasAttribute('data-unsaved'), 'recreated queue time fields retain the saved baseline');
    click('删除定时'); await settle();
    model.queueSchedules.value = [original as never]; await settle();
    holder.querySelector<HTMLButtonElement>('.schedule-heading button')!.click(); await settle();
    check(!model.edited && !holder.querySelector('nexus-time-picker')?.hasAttribute('data-unsaved'),
      'undoing an existing schedule deletion restores its business and field baselines');
  } finally { app.unmount(); holder.remove(); store.dispose(); }
}
