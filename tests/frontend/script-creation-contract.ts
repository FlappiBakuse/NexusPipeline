import { createApp, h, nextTick, ref } from 'vue';
import ScriptTypes from '../../apps/web/src/pages/ScriptTypes.vue';
import { ControlClient } from '../../apps/web/src/control/client';
import type { ExtensionState } from '../../apps/web/src/control/extensions';
import { createStore } from '../../apps/web/src/app/store';
import { AutomationEditor, automationSession } from '../../apps/web/src/pages/automation-editor';

export async function scriptCreationContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  let respond: (value: Response) => void = () => {};
  let reads = 0;
  const send: typeof fetch = () => new Promise(resolve => { reads++; respond = resolve; });
  const store = createStore(new ControlClient(location.origin, send));
  store.state.capabilities = { instanceId: 'script-fixture', hostVersion: '0.18.0', capabilities: ['extensions.read', 'automation.declare'] };
  store.state.automation = { catalogRevision: 1, roots: [], revisions: [], bundle: {
    format: 'nexus.automation', version: 1, bundleId: crypto.randomUUID(), createdAt: new Date().toISOString(),
    roots: [], scripts: [], users: [], bindings: [], queues: [], schedules: [], attachments: []
  } } as never;
  const visible = ref(false), mount = document.createElement('section'); fixture.append(mount);
  const installed = ref<readonly ExtensionState[]>();
  const app = createApp({ render: () => h(ScriptTypes, { store, visible: visible.value, active: true,
    installed: installed.value, 'onUpdate:installed': value => { installed.value = value; } }) });
  app.mount(mount);
  const hasTypes = (...labels: string[]) => JSON.stringify([...mount.querySelectorAll('.type-choice strong')]
    .map(item => item.textContent?.trim()).sort()) === JSON.stringify(labels.sort());
  const catalog = { hasReleaseTrust: true, items: ['alpha', 'beta'].map(extensionId => ({ extensionId,
    installedRevision: 1, isInstalled: true, desiredEnabled: true, activationId: 'active', unavailableReason: null,
    kind: 'data-specialized', capabilities: ['task.adapt'] })) };
  try {
    check(mount.querySelectorAll('.type-choice').length === 0, 'cold type loading does not show a misleading generic-only list');
    respond(Response.json(catalog));
    for (let i = 0; i < 50 && mount.querySelectorAll('.type-choice').length !== 3; i++)
      await new Promise(resolve => setTimeout(resolve, 10));
    check(hasTypes('通用脚本', 'alpha 专项', 'beta 专项'), 'all types preload while the picker is closed');
    visible.value = true; await nextTick();
    check(hasTypes('通用脚本', 'alpha 专项', 'beta 专项') && installed.value?.length === 2,
      'opening shows cached cards and shares the installed metadata with the editor owner');
    visible.value = false; await nextTick(); visible.value = true; await nextTick();
    check(hasTypes('通用脚本', 'alpha 专项', 'beta 专项') && reads === 1,
      'closing and reopening without a generation change do not query extensions again');
    store.state.events = { extensionGeneration: 2 };
    await nextTick();
    check(reads === 2, 'an updated extension generation refreshes the type list');
    respond(Response.json({ ...catalog, items: [catalog.items[1]] }));
    for (let i = 0; i < 50 && mount.querySelectorAll('.type-choice').length !== 2; i++)
      await new Promise(resolve => setTimeout(resolve, 10));
    check(hasTypes('通用脚本', 'beta 专项'), 'refresh removes types no longer available');
    const editor = new AutomationEditor(store, () => 'scripts', automationSession(store, 'scripts'));
    editor.createScript('alpha');
    editor.draft.value = { ...editor.draft.value, name: 'Alpha draft' }; editor.editing.value = false;
    editor.createScript('beta');
    check(editor.draft.value.extensionId === 'beta' && editor.draft.value.name !== 'Alpha draft', 'beta opens with its own draft fields');
    editor.editing.value = false; editor.createScript('alpha');
    check(editor.draft.value.extensionId === 'alpha' && editor.draft.value.name === 'Alpha draft', 'returning to alpha preserves its unsaved draft');
    editor.discard(); editor.createScript('alpha');
    check(editor.draft.value.extensionId === 'alpha' && editor.draft.value.name !== 'Alpha draft', 'reopening alpha after cancellation starts with a fresh draft');
    editor.editing.value = false; editor.createScript();
    check(editor.draft.value.kind !== 'specialized' && !editor.draft.value.extensionId, 'generic selection does not inherit a specialized type');
  } finally { app.unmount(); mount.remove(); store.dispose(); }
}
