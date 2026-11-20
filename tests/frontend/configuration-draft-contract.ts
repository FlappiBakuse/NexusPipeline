import AdapterEditorPanel from '../../apps/web/src/pages/AdapterEditorPanel.vue';
import { prepareAppExit } from '../../apps/web/src/app/exit-preparation';
import { bindingEditorFixture, draftFixture, mountAdapter, exitDecision, settle } from './configuration-draft-fixture';

type Check = (value: unknown, name: string) => void;
export async function configurationDraftContract(parent: HTMLElement, check: Check): Promise<void> {
  await bindingEditor(parent, check);
  await adapterChoices(parent, check);
  await untouched(parent, check);
  await exitReadiness(check);
}
async function bindingEditor(parent: HTMLElement, check: Check): Promise<void> {
  const fixture = bindingEditorFixture(parent);
  try {
    await settle();
    const modal = fixture.holder.querySelector('nexus-modal')!;
    check(modal.querySelector('p')?.textContent?.includes('主程序已启动') &&
      modal.querySelector('p')?.textContent?.includes('快照'),
      'a launched binding editor shows save and cancel instructions in the body');
    const actions = [...modal.querySelectorAll('button')];
    check(actions.map(button => button.textContent?.trim()).join(',') === '关闭,取消,保存' &&
      actions.every(button => getComputedStyle(button).minWidth === '80px'),
      'the three binding editor actions use the common modal width');
    actions[2]!.focus();
    await new Promise(resolve => setTimeout(resolve, 450));
    check(!modal.querySelector('nexus-tooltip'), 'the save action has no focus bubble');
    actions[0]!.click(); await settle();
    fixture.reopen(); await settle();
    check(fixture.writes.length === 1 && fixture.holder.textContent?.includes('主程序已启动'),
      'closing and reopening shows the retained editing session without launching a second writer');
    [...fixture.holder.querySelectorAll('button')].find(button => button.textContent?.trim() === '保存')!.click();
    await settle();
    check(fixture.writes.at(-1)?.endsWith('/session/save-binding') && !fixture.holder.querySelector('nexus-modal'),
      'save finishes the original session and closes the editor');
  } finally { fixture.dispose(); }
}
async function adapterChoices(parent: HTMLElement, check: Check): Promise<void> {
  const fixture = draftFixture();
  let view = mountAdapter(AdapterEditorPanel, fixture, parent);
  try {
    await settle();
    view.holder.querySelector<HTMLInputElement>('input[type=checkbox]')!.click();
    await settle(); view.dispose();
    view = mountAdapter(AdapterEditorPanel, fixture, parent); await settle();
    check(view.holder.querySelector<HTMLInputElement>('input[type=checkbox]')!.checked,
      'closing and reopening specialized configuration retains the resource choice');
    const decision = await exitDecision(fixture.store);
    check(!decision.prompted && decision.accepted && fixture.writes.length === 0,
      'specialized configuration choices never ask for an exit decision or write configuration');
  } finally { view.dispose(); fixture.store.dispose(); }
}
async function untouched(parent: HTMLElement, check: Check): Promise<void> {
  const fixture = draftFixture();
  let view = mountAdapter(AdapterEditorPanel, fixture, parent);
  try {
    await settle();
    view.dispose();
    view = mountAdapter(AdapterEditorPanel, fixture, parent);
    await settle();
    check(!view.holder.querySelector('nexus-toast.draft-restored-toast') && !view.holder.querySelector('[data-unsaved=true]'),
      'untouched specialized configuration reopens without a draft or yellow fields');
  } finally { view.dispose(); fixture.store.dispose(); }
}
async function exitReadiness(check: Check): Promise<void> {
  const client = { pendingWrites: 0, canExitMaintenance: () => true };
  const decide = prepareAppExit(client);
  check(await decide('close') === 'ready' && !document.querySelector('nexus-confirm-dialog'),
    'normal exit readiness has no user decision stage');
  client.pendingWrites = 1;
  check(await decide('close') === 'pending-writes', 'actual in-flight writes remain an explicit exit blocker');
  check(await decide('maintenance') === 'ready', 'the signed restart handoff keeps its own waiting RPC exemption');
}
