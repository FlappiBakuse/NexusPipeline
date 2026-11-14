import { createApp, h, nextTick, type Component } from 'vue';
import FormSelect from '../../apps/web/src/components/FormSelect';
import { ControlClient } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';
import type { ConfigurationOverview } from '../../apps/web/src/pages/configuration-types';
import { prepareAppExit } from '../../apps/web/src/app/exit-preparation';

export const settle = async () => {
  for (let index = 0; index < 8; index++) {
    await nextTick(); await new Promise(resolve => setTimeout(resolve, 0));
  }
};
export function draftFixture() {
  const writes: string[] = [];
  let readGate: Promise<void> | undefined;
  const send: typeof fetch = async (input, options) => {
    const route = new URL(String(input)).pathname;
    if (route.endsWith('/files') && readGate) await readGate;
    if (options?.method && options.method !== 'GET') writes.push(route);
    if (route === '/control/v1/extensions') return Response.json({ hasReleaseTrust: false,
      items: [{ extensionId: 'fixture', installedRevision: 1, isInstalled: true, desiredEnabled: true,
        activationId: 'active', unavailableReason: null, capabilities: [] }] });
    if (route.endsWith('/fixture/details')) return Response.json({ extensionId: 'fixture', packageVersion: '0.5.0',
      manifest: { format: 'nexus.extension.manifest', version: 1, extensionId: 'fixture', displayName: 'Fixture',
        artifactName: 'Fixture', minHostVersion: '0.18.0', packageVersion: '0.5.0', kind: 'data-specialized' },
      documents: {}, diagnosticLocales: {} });
    if (route.endsWith('/files')) return Response.json({ items: [{ position: 0, relativePath: 'config.json',
      sizeBytes: 16, sha256: 'a'.repeat(64) }], nextPosition: null });
    if (route.endsWith('/document')) return Response.json({ revision: 1, sourceSha256: 'a'.repeat(64), value: { enabled: true } });
    if (route.endsWith('/editor/begin')) return Response.json({ sessionId: 'session', revision: 1, phase: 'editing' });
    if (route.startsWith('/control/v1/operations/')) return Response.json({ operationId: route.split('/').pop(),
      kind: 'configuration.save', status: 'completed' });
    return Response.json({ code: 'configuration_fixture_unavailable' }, { status: 403 });
  };
  const store = createStore(new ControlClient(location.origin, send));
  store.state.capabilities = { instanceId: 'exit-fixture', hostVersion: '0.18.0',
    capabilities: ['configuration.read', 'configuration.edit', 'configuration.native.open', 'configuration.repair'] };
  store.state.automation = { catalogRevision: 1, revisions: [], roots: [{ id: 'root', revision: 1, path: 'D:/fixture' }],
    bundle: { scripts: [{ id: 'script', executable: { rootId: 'root', relative: 'editor.exe' } }],
      users: [], bindings: [], queues: [], schedules: [] } } as never;
  return { store, writes, holdRead() {
    let release!: () => void;
    readGate = new Promise<void>(resolve => { release = resolve; });
    return () => { readGate = undefined; release(); };
  } };
}
export function mountEditor(component: Component, fixture: ReturnType<typeof draftFixture>, kind: string, parent: HTMLElement) {
  const holder = document.createElement('section'); parent.append(holder);
  const overview: ConfigurationOverview = { bindingId: 'binding-' + kind, bindingRevision: 1, scriptId: 'script',
    scriptRevision: 1, kind: kind === 'value' ? 'generic' : 'specialized', extensionId: kind === 'value' ? null : 'fixture',
    resources: [{ resourceId: 'resource', declarationIndex: 0, label: 'Saved configuration', revision: 1,
      isSaved: true, shape: 'file', fileCount: 1, relativePath: 'config.json', syntax: 'json' }] };
  const app = createApp({ render: () => h(component, { store: fixture.store, overview, visible: true }) });
  app.component('FormSelect', FormSelect); app.mount(holder);
  return { holder, dispose() { app.unmount(); holder.remove(); } };
}
export function selectChoice(holder: HTMLElement, label: string, value: string) {
  const field = [...holder.querySelectorAll('label')].find(item => item.textContent?.includes(label));
  if (!field) throw new Error('Missing configuration choice: ' + label);
  field.querySelector('nexus-select')!.dispatchEvent(new CustomEvent('nexus-change',
    { detail: { value }, bubbles: true, composed: true }));
}
export function editValue(holder: HTMLElement, value: string) {
  const input = holder.querySelector('nexus-text-area[aria-label="新值"]');
  if (!input) throw new Error('Missing configuration value input');
  input.dispatchEvent(new CustomEvent('nexus-change', { detail: { value }, bubbles: true, composed: true }));
}
export async function exitDecision(store: ReturnType<typeof draftFixture>['store']) {
  const result = prepareAppExit(store.client)('close');
  await settle();
  const dialog = document.querySelector('nexus-confirm-dialog');
  return { prompted: !!dialog, accepted: await result === 'ready' };
}
