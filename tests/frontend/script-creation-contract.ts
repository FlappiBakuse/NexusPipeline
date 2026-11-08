import { createApp, h, nextTick, ref } from 'vue';
import ScriptTypes from '../../apps/web/src/pages/ScriptTypes.vue';
import { ControlClient } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';
import { AutomationEditor } from '../../apps/web/src/pages/automation-editor';
import { automationSession } from '../../apps/web/src/pages/automation-session';

export async function scriptCreationContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  let respond: (value: Response) => void = () => {};
  const send: typeof fetch = () => new Promise(resolve => { respond = resolve; });
  const store = createStore(new ControlClient(location.origin, send));
  store.state.capabilities = { instanceId: 'script-fixture', hostVersion: '0.18.0', capabilities: ['extensions.read', 'automation.declare'] };
  store.state.automation = { catalogRevision: 1, roots: [], revisions: [], bundle: {
    format: 'nexus.automation', version: 1, bundleId: crypto.randomUUID(), createdAt: new Date().toISOString(),
    roots: [], scripts: [], users: [], bindings: [], queues: [], schedules: [], attachments: []
  } } as never;
  const visible = ref(false), mount = document.createElement('section'); fixture.append(mount);
  const app = createApp({ render: () => h(ScriptTypes, { store, visible: visible.value, active: true }) });
  app.mount(mount);
  const catalog = { hasReleaseTrust: true, items: ['alpha', 'beta'].map(extensionId => ({ extensionId,
    installedRevision: 1, isInstalled: true, desiredEnabled: true, activationId: 'active', unavailableReason: null,
    kind: 'data-specialized', capabilities: ['task.adapt'] })) };
  try {
    check(mount.querySelectorAll('.type-choice').length === 0, 'cold type loading does not show a misleading generic-only list');
    respond(Response.json(catalog));
    for (let i = 0; i < 50 && mount.querySelectorAll('.type-choice').length !== 3; i++)
      await new Promise(resolve => setTimeout(resolve, 10));
    check(mount.querySelectorAll('.type-choice').length === 3, 'all types preload while the picker is closed');
    visible.value = true; await nextTick();
    check(mount.querySelectorAll('.type-choice').length === 3, 'opening shows every cached card before a delayed refresh finishes');
    visible.value = false; await nextTick(); visible.value = true; await nextTick();
    check(mount.querySelectorAll('.type-choice').length === 3, 'reopening retains the complete type list');
    respond(Response.json({ ...catalog, items: [catalog.items[1]] }));
    for (let i = 0; i < 50 && mount.querySelectorAll('.type-choice').length !== 2; i++)
      await new Promise(resolve => setTimeout(resolve, 10));
    check(mount.querySelectorAll('.type-choice').length === 2, 'refresh removes types no longer available');
    const editor = new AutomationEditor(store, () => 'scripts', automationSession(store, 'scripts'));
    editor.createScript('alpha'); const alpha = editor.selected.value;
    editor.draft.value = { ...editor.draft.value, name: 'Alpha draft' }; editor.editing.value = false;
    editor.createScript('beta');
    check(editor.draft.value.extensionId === 'beta' && editor.selected.value !== alpha, 'a different type creates its own editor');
    editor.editing.value = false; editor.createScript('alpha');
    check(editor.selected.value === alpha && editor.draft.value.name === 'Alpha draft', 'returning to a type preserves its unsaved draft');
    editor.editing.value = false; editor.createScript();
    check(editor.draft.value.kind !== 'specialized' && !editor.draft.value.extensionId, 'generic selection does not inherit a specialized type');
    editor.discard(); editor.createScript('alpha'); editor.discard(); editor.createScript('alpha');
    check(editor.selected.value !== alpha && editor.draft.value.name !== 'Alpha draft', 'cancelled drafts are not resurrected');
  } finally { app.unmount(); mount.remove(); store.dispose(); }
}
