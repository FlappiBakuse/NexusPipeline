import { createApp, h, nextTick, ref } from 'vue';
import ScriptTypes from '../../apps/web/src/pages/ScriptTypes.vue';
import ScriptSourceFields from '../../apps/web/src/pages/ScriptSourceFields.vue';
import { ControlClient } from '../../apps/web/src/control/client';
import { clearExtensionIcons } from '../../apps/web/src/control/extension-icons';
import type { ExtensionState } from '../../apps/web/src/control/extensions';
import { createStore } from '../../apps/web/src/app/store';
import { AutomationEditor, automationSession } from '../../apps/web/src/pages/automation-editor';
import { scriptPaths } from '../../apps/web/src/pages/script-paths';

export async function scriptCreationContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  let respond: (value: Response) => void = () => {};
  let reads = 0;
  const send: typeof fetch = async input => {
    if (new URL(String(input)).pathname.endsWith('/icon')) return new Response(null, { status: 204 });
    return new Promise(resolve => { reads++; respond = resolve; });
  };
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
  } finally { app.unmount(); mount.remove(); store.dispose(); clearExtensionIcons(); }
  await okwwPreviewContract(fixture, check);
}

async function okwwPreviewContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const first = 'D:/fixtures/okww-first', second = 'D:/fixtures/okww-second';
  const pending = new Map<string, () => void>();
  const returned = new Set<string>();
  const store = createStore(new ControlClient(location.origin, async (input, init) => {
    const path = new URL(String(input)).pathname;
    if (path.endsWith('/extensions')) return Response.json({ hasReleaseTrust: true, items: [{
      extensionId: 'okww', installedRevision: 1, isInstalled: true, desiredEnabled: true, activationId: 'active',
      kind: 'data-specialized', capabilities: ['task.adapt']
    }] });
    if (!path.endsWith('/scripts/discover')) throw Error('unexpected_source_preview_request');
    const { operationId, rootPath } = JSON.parse(String(init?.body));
    const response = await new Promise<Response>(resolve => pending.set(rootPath, () => resolve(Response.json({
      rootPath, installedRevision: 1,
      discovery: { format: 'nexus.script.source', version: 1, direction: 'response', extensionId: 'okww',
        purpose: 'edit', operationId, candidates: [], diagnostics: ['okww_runtime_profile_unverified'],
        editor: { executable: 'ok-ww.exe', arguments: [], scopes: ['configs'], canCreate: false, exclusiveProgram: false },
        accountPreview: rootPath === first
          ? { state: 'empty', mode: 'disabled', accounts: [] }
          : { state: 'ready', mode: 'enabled', accounts: [
            { enabled: true, task: 'DailyTask', overrideCount: 2 },
            { enabled: false, task: 'loginOnly', overrideCount: 0 }
          ] } }
    }))));
    returned.add(rootPath);
    return response;
  }));
  store.state.capabilities = { instanceId: 'okww-fixture', hostVersion: '0.18.0',
    capabilities: ['extensions.read', 'configuration.read', 'automation.declare'] };
  store.state.automation = { catalogRevision: 1, roots: [], revisions: [], bundle: {
    format: 'nexus.automation.bundle', version: 1, exportKind: 'definitions', bundleId: crypto.randomUUID(),
    createdAt: new Date().toISOString(), roots: [], scripts: [], users: [], bindings: [], queues: [], schedules: [], attachments: []
  } };
  const editor = new AutomationEditor(store, () => 'scripts', automationSession(store, 'scripts'));
  editor.createScript('okww');
  const model = editor.draft.value;
  if (!('executable' in model)) throw Error('missing_script_draft');
  const paths = scriptPaths(store, model);
  paths.root = first;
  paths.executable = 'ok-ww.exe';
  const mount = document.createElement('div'); fixture.append(mount);
  let selected = 0;
  const app = createApp({ render: () => h(ScriptSourceFields, { store, modelValue: model,
    'onUpdate:modelValue': () => { selected++; } }) });
  app.mount(mount);
  const settle = async (ready: () => boolean) => {
    for (let i = 0; i < 30 && !ready(); i++) await new Promise(resolve => setTimeout(resolve, 10));
  };
  try {
    await settle(() => pending.has(first));
    paths.root = second;
    await settle(() => pending.has(second));
    paths.executable = 'custom-saved.exe';
    pending.get(second)!();
    const rows = () => [...mount.querySelectorAll('ol li')].map(row => row.textContent || '');
    await settle(() => rows().length === 2);
    check(rows().length === 2 && rows()[0]!.includes('2') && rows()[1]!.includes('仅登录'),
      'OkWW renders ordered read-only accounts with their task and override summary');
    pending.get(first)!();
    await settle(() => returned.has(first));
    await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
    await nextTick();
    check(rows().length === 2, 'a late preview for the previous root cannot replace the current account rows');
    check(selected === 1 && paths.executable === 'custom-saved.exe' && !mount.querySelector('button, input'),
      'the preview has no selection control and preserves the previously selected executable');
  } finally { app.unmount(); mount.remove(); store.dispose(); }
}
