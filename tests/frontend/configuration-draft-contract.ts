import AdapterEditorPanel from '../../apps/web/src/pages/AdapterEditorPanel.vue';
import NativeEditorPanel from '../../apps/web/src/pages/NativeEditorPanel.vue';
import ValueEditorPanel from '../../apps/web/src/pages/ValueEditorPanel.vue';
import { prepareAppExit } from '../../apps/web/src/app/exit-preparation';
import { draftFixture, mountEditor, selectChoice, editValue, exitDecision, settle } from './configuration-draft-fixture';

type Check = (value: unknown, name: string) => void;
export async function configurationDraftContract(parent: HTMLElement, check: Check): Promise<void> {
  await adapterChoices(parent, check);
  await nativeChoices(parent, check);
  await valueChoices(parent, check);
  await untouched(parent, check);
  await exitReadiness(check);
}
async function adapterChoices(parent: HTMLElement, check: Check): Promise<void> {
  const fixture = draftFixture();
  let view = mountEditor(AdapterEditorPanel, fixture, 'adapter', parent);
  try {
    await settle();
    view.holder.querySelector<HTMLInputElement>('input[type=checkbox]')!.click();
    await settle(); view.dispose();
    view = mountEditor(AdapterEditorPanel, fixture, 'adapter', parent); await settle();
    check(view.holder.querySelector<HTMLInputElement>('input[type=checkbox]')!.checked,
      'closing and reopening specialized configuration retains the resource choice');
    const decision = await exitDecision(fixture.store);
    check(!decision.prompted && decision.accepted && fixture.writes.length === 0,
      'specialized configuration choices never ask for an exit decision or write configuration');
  } finally { view.dispose(); fixture.store.dispose(); }
}
async function nativeChoices(parent: HTMLElement, check: Check): Promise<void> {
  const fixture = draftFixture();
  let view = mountEditor(NativeEditorPanel, fixture, 'native', parent);
  try {
    await settle();
    check(!view.holder.querySelector('.draft-restored'), 'a first native configuration opening has no recovery reminder');
    const input = view.holder.querySelector<HTMLInputElement>('input[placeholder="tools/editor.exe"]')!;
    input.value = 'tools/retained.exe'; input.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    check(!input.hasAttribute('data-unsaved'), 'first-session native editor changes are not highlighted');
    view.dispose(); view = mountEditor(NativeEditorPanel, fixture, 'native', parent); await settle();
    check(view.holder.querySelector<HTMLInputElement>('input[placeholder="tools/editor.exe"]')!.value === 'tools/retained.exe',
      'native editor launch input remains available after closing its view');
    check(!!view.holder.querySelector('.draft-restored') &&
      view.holder.querySelector('input[placeholder="tools/editor.exe"]')!.getAttribute('data-unsaved') === 'true',
      'a reopened native configuration preserves its recovery reminder and original field baseline');
    [...view.holder.querySelectorAll('button')].find(button => button.textContent?.includes('打开编辑器'))!.click();
    await settle(); view.dispose();
    view = mountEditor(NativeEditorPanel, fixture, 'native', parent); await settle();
    check(view.holder.textContent?.includes('editing') && fixture.writes.length === 1,
      'reopening an actual native session restores its original session without another launch');
    const decision = await exitDecision(fixture.store);
    check(!decision.prompted && decision.accepted,
      'native session evidence is left to Host shutdown authorization without a draft confirmation');
  } finally { view.dispose(); fixture.store.dispose(); }
}
async function valueChoices(parent: HTMLElement, check: Check): Promise<void> {
  const fixture = draftFixture();
  let view = mountEditor(ValueEditorPanel, fixture, 'value', parent);
  const raw = () => (view.holder.querySelector('nexus-text-area[aria-label="新值"]') as HTMLElement & { value: string })?.value;
  try {
    await settle(); selectChoice(view.holder, '选择资源', '1'); await settle();
    selectChoice(view.holder, '选择字段', '1'); await settle();
    editValue(view.holder, 'false'); await settle();
    check(!view.holder.querySelector('nexus-text-area[aria-label="新值"]')!.hasAttribute('data-unsaved'),
      'first-session generic configuration changes are not highlighted');
    view.dispose();
    const release = fixture.holdRead();
    view = mountEditor(ValueEditorPanel, fixture, 'value', parent); await settle();
    try {
      const decision = await exitDecision(fixture.store);
      check(!decision.prompted && decision.accepted, 'an asynchronous draft restore never opens an exit confirmation');
    } finally { release(); }
    await settle();
    check(raw() === 'false', 'reopening a configuration field restores the unsaved JSON text');
    check(!!view.holder.querySelector('.draft-restored') &&
      view.holder.querySelector('nexus-text-area[aria-label="新值"]')!.getAttribute('data-unsaved') === 'true',
      'a reopened generic configuration identifies its draft and retains the original changed-field baseline');
    editValue(view.holder, 'true'); await settle();
    check(!view.holder.querySelector('nexus-text-area[aria-label="新值"]')!.hasAttribute('data-unsaved'),
      'reverting a generic configuration value removes its highlight');
    editValue(view.holder, '{'); await settle(); view.dispose();
    view = mountEditor(ValueEditorPanel, fixture, 'value', parent); await settle();
    check(raw() === '{', 'an invalid JSON draft survives closing and reopening');
    const decision = await exitDecision(fixture.store);
    check(!decision.prompted && decision.accepted && fixture.writes.length === 0,
      'edited and invalid configuration drafts leave exit confirmation free of database writes');
  } finally { view.dispose(); fixture.store.dispose(); }
}
async function untouched(parent: HTMLElement, check: Check): Promise<void> {
  const fixture = draftFixture();
  try {
    for (const [component, kind] of [[ValueEditorPanel, 'value'], [NativeEditorPanel, 'native'], [AdapterEditorPanel, 'adapter']] as const) {
      let view = mountEditor(component, fixture, kind, parent); await settle(); view.dispose();
      view = mountEditor(component, fixture, kind, parent); await settle();
      check(!view.holder.querySelector('.draft-restored') && !view.holder.querySelector('[data-unsaved=true]'),
        'untouched ' + kind + ' configuration reopens without a draft or yellow fields');
      view.dispose();
    }
    let view = mountEditor(ValueEditorPanel, fixture, 'value', parent); await settle();
    selectChoice(view.holder, '选择资源', '1'); await settle();
    selectChoice(view.holder, '选择字段', '1'); await settle(); view.dispose();
    view = mountEditor(ValueEditorPanel, fixture, 'value', parent); await settle();
    check(!view.holder.querySelector('.draft-restored'), 'browsing saved JSON fields does not retain a configuration draft');
    view.dispose();
  } finally { fixture.store.dispose(); }
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
