import { createApp, h } from 'vue';
import { contractFingerprint, sdkVersion, type ExtensionManifest, type EditorDraftScope, type UiElement } from '@nexuspipeline/extension-sdk';
import { FrontendRegistry } from '../../apps/web/src/extensions/frontend-registry';
import { EditorDraftStore } from '../../apps/web/src/app/editor-drafts';
import { AutomationEditor } from '../../apps/web/src/pages/automation-editor';
import { automationSession } from '../../apps/web/src/pages/automation-session';
import AutomationEditorDialog from '../../apps/web/src/pages/AutomationEditorDialog.vue';
import { draftFixture, exitDecision, settle } from './configuration-draft-fixture';
import { draftPresentation } from '../../apps/web/src/app/draft-presentation';
import { privateDraftContract } from './private-draft-contract';

type Check = (value: unknown, name: string) => void;
export async function draftLifecycleContract(parent: HTMLElement, check: Check): Promise<void> {
  await newAccount(parent, check);
  await changedFields(parent, check);
  await pathField(parent, check);
  await extensionMemory(parent, check);
  await privateDraftContract(parent, check);
}
async function newAccount(parent: HTMLElement, check: Check) {
  const fixture = draftFixture(), store = fixture.store;
  const session = automationSession(store, 'users');
  const model = new AutomationEditor(store, () => 'users', session);
  const holder = document.createElement('section'); parent.append(holder);
  const app = createApp({ render: () => model.editing.value ? h(AutomationEditorDialog,
    { store, kind: 'users', visible: true, model, session, onClose: () => { model.editing.value = false; } }) : null });
  app.mount(holder);
  const close = async () => {
    const modal = holder.querySelector('nexus-modal')!;
    modal.shadowRoot!.querySelector<HTMLButtonElement>('button[aria-label="关闭"],button[aria-label="Close"]')!.click(); await settle();
  };
  try {
    model.edit(); await settle(); const id = model.selected.value, key = model.draftKey.value;
    await close();
    check(!store.drafts.has(key), 'closing an untouched form releases its editing buffer');
    const blankExit = await exitDecision(store);
    check(!blankExit.prompted && blankExit.accepted && fixture.writes.length === 0,
      'closing an untouched new account exits without a draft confirmation or save');
    model.edit(); await settle();
    check(!holder.querySelector('.draft-restored'), 'reopening an untouched new form has no draft reminder');
    check(!holder.querySelector('[data-unsaved=true]'), 'new drafts do not highlight fields');
    check(!store.dirty.has(model.draftKey.value), 'an untouched new form has no edited state');
    holder.querySelector('nexus-text-input')!.dispatchEvent(new CustomEvent('nexus-change', { detail: { value: 'Retained account' } }));
    await settle(); await close(); model.edit(); await settle();
    check((holder.querySelector('nexus-text-input') as HTMLElement & { value: string }).value === 'Retained account',
      'the real close button preserves edited account fields');
    const scope = store.editors.target('exit-fixture', 'users', id).child('extension/fixture');
    const secret = scope.protect('credential', () => ({ value: crypto.randomUUID() }), value => { value.value = ''; });
    [...holder.querySelectorAll('button')].find(button => button.textContent?.trim() === '取消')!.click(); await settle();
    model.edit(); await settle();
    check(model.selected.value !== id && model.draft.value.name !== 'Retained account' && secret.value === '',
      'explicit cancellation clears the account and its protected child draft');
  } finally { app.unmount(); holder.remove(); store.dispose(); }
}
async function changedFields(parent: HTMLElement, check: Check) {
  const fixture = draftFixture(), store = fixture.store, session = automationSession(store, 'users');
  const model = new AutomationEditor(store, () => 'users', session);
  model.edit();
  const saved = { ...model.draft.value, name: 'Saved account', notes: 'Saved note' };
  model.discard();
  store.state.automation = { ...store.state.automation!,
    bundle: { ...store.state.automation!.bundle, users: [saved as never] } };
  const holder = document.createElement('section'); parent.append(holder);
  const app = createApp({ render: () => model.editing.value ? h(AutomationEditorDialog,
    { store, kind: 'users', visible: true, model, session, onClose: () => { model.editing.value = false; } }) : null });
  app.mount(holder);
  const edit = async (value: string) => {
    holder.querySelector('nexus-text-input')!.dispatchEvent(new CustomEvent('nexus-change',
      { detail: { value }, bubbles: true, composed: true })); await settle();
  };
  try {
    model.edit(saved); await settle();
    check(!holder.querySelector('.draft-restored'), 'the first existing-account opening has no restored reminder');
    await edit('Changed account');
    check(!holder.querySelector('nexus-text-input')!.hasAttribute('data-unsaved'),
      'first-session edits do not show restored-draft highlighting');
    check(!holder.querySelector('nexus-text-area')!.hasAttribute('data-unsaved'),
      'unchanged fields stay unhighlighted');
    model.draft.value = { ...model.draft.value, notes: 'Programmatic note' };
    await settle();
    check(!holder.querySelector('nexus-text-area')!.hasAttribute('data-unsaved'),
      'programmatic changes wait for draft restoration before highlighting');
    model.draft.value = { ...model.draft.value, notes: 'Saved note' }; await settle();
    model.editing.value = false; await settle(); model.edit(saved); await settle();
    check(!!holder.querySelector('.draft-restored') && holder.querySelector('nexus-text-input')!.hasAttribute('data-unsaved'),
      'restored existing edits retain their reminder and original field baseline');
    await edit('Saved account');
    check(!holder.querySelector('nexus-text-input')!.hasAttribute('data-unsaved'),
      'returning to the saved field value clears its highlight');
    model.discard(); await settle(); model.edit(saved); await settle();
    check(!holder.querySelector('.draft-restored') && !holder.querySelector('[data-unsaved=true]'),
      'explicit cancellation clears presentation state for the target');
  } finally { app.unmount(); holder.remove(); store.dispose(); }
}
async function pathField(parent: HTMLElement, check: Check) {
  const memory = new EditorDraftStore(() => {});
  const holder = document.createElement('section'); parent.append(holder);
  const input = document.createElement('nexus-path-picker') as UiElement<'nexus-path-picker'>;
  input.setAttribute('aria-label', 'Editor path'); input.value = 'D:/saved/editor.exe'; holder.append(input);
  const presentation = draftPresentation(memory.target('field-fixture', 'scripts', 'existing'), true);
  try {
    presentation.attach(holder); input.value = 'D:/changed/editor.exe';
    check(!input.hasAttribute('data-unsaved'), 'first-session path changes remain unhighlighted');
    presentation.detach(); presentation.attach(holder);
    check(presentation.state.restored && input.getAttribute('data-unsaved') === 'true',
      'a retained path picker keeps its original saved baseline');
    input.value = 'D:/saved/editor.exe';
    check(!input.hasAttribute('data-unsaved'), 'restoring the saved path clears its highlight');
  } finally { memory.dispose(); holder.remove(); }
}
async function extensionMemory(parent: HTMLElement, check: Check) {
  const errors: unknown[] = [], memory = new EditorDraftStore(error => errors.push(error));
  const registry = new FrontendRegistry({ locale: 'zh-CN', appearance: 'dark', translate: (_id, key) => key }, memory);
  const manifest: ExtensionManifest = { format: 'nexus.extension.manifest', version: 1, extensionId: 'draft-fixture',
    artifactName: 'Fixture', displayName: 'Fixture', packageVersion: '0.5.0', kind: 'managed-code', minHostVersion: '0.18.0',
    sdkVersion, contractFingerprint, capabilities: ['ui.contribute'], permissions: {},
    managed: { entryAssembly: 'Fixture.dll', entryType: 'Fixture' }, frontend: { entry: 'web/main.js', styles: [], sdkVersion } };
  const visits: { ordinary: { text: string }; secret: { value: string }; scope: EditorDraftScope }[] = [];
  const active = await registry.activate(manifest, { extensionId: manifest.extensionId, sdkVersion, contractFingerprint,
    activate(context) { context.registerSurface({ id: 'editor', target: 'automation.script.editor', mount(surface) {
      visits.push({ ordinary: surface.drafts.retain('form', () => ({ text: '' })),
        secret: surface.drafts.protect('credential', () => ({ value: '' }), value => { value.value = ''; }), scope: surface.drafts });
    } }); } });
  const holder = document.createElement('section'); parent.append(holder);
  const context = { surfaceId: 'automation.script.editor' as const, instanceId: 'draft-instance', entityId: 'first' };
  try {
    const first = await registry.mount(manifest.extensionId, 'editor', holder, context);
    const entry = visits.at(-1)!; entry.ordinary.text = 'retained'; entry.secret.value = crypto.randomUUID();
    let serialized = false;
    try { JSON.stringify(entry.secret); serialized = true; } catch { }
    check(!serialized && !JSON.stringify(entry.ordinary).includes(entry.secret.value), 'protected drafts reject ordinary JSON serialization');
    await first.scope.dispose();
    const reopened = await registry.mount(manifest.extensionId, 'editor', holder, { ...context });
    check(visits.at(-1)!.ordinary.text === 'retained' && visits.at(-1)!.secret.value === entry.secret.value,
      'actual extension surface disposal and remount retain ordinary and sensitive drafts');
    memory.saved(context.instanceId, 'scripts', context.entityId);
    check(entry.ordinary.text === 'retained' && !!entry.secret.value, 'parent definition saves preserve independently edited plugin fields');
    let pending = true; const release = entry.scope.guardCancel(() => !pending);
    let refused = false;
    try { memory.settle(context.instanceId, 'scripts', context.entityId, 'cancel'); } catch { refused = true; }
    check(refused && !!entry.secret.value, 'an unresolved operation prevents cancellation from losing its retained draft');
    pending = false; release(); await reopened.scope.dispose();
    memory.settle(context.instanceId, 'scripts', context.entityId, 'cancel');
    check(entry.secret.value === '', 'parent cancellation clears protected values after operations settle');
    const other = await registry.mount(manifest.extensionId, 'editor', holder, { ...context, entityId: 'other' });
    check(visits.at(-1)!.ordinary.text === '' && visits.at(-1)!.secret.value === '', 'different editor targets have independent drafts');
    visits.at(-1)!.secret.value = crypto.randomUUID(); const last = visits.at(-1)!;
    await other.scope.dispose(); await active.dispose();
    check(last.secret.value === '' && errors.length === 0, 'extension retirement releases protected drafts and their owners');
  } finally { await active.dispose(); memory.dispose(); holder.remove(); }
}
