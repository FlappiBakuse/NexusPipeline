import { createApp, h, nextTick, ref, type Component } from 'vue';
import BindingEditorDialog from '../../apps/web/src/pages/BindingEditorDialog.vue';
import { FrontendRegistry } from '../../apps/web/src/extensions/frontend-registry';
import { registryKey } from '../../apps/web/src/extensions/registry-context';
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
  const send: typeof fetch = async (input, options) => {
    const route = new URL(String(input)).pathname;
    if (options?.method && options.method !== 'GET') writes.push(route);
    if (route === '/control/v1/extensions') return Response.json({ hasReleaseTrust: false,
      items: [{ extensionId: 'fixture', installedRevision: 1, isInstalled: true, desiredEnabled: true,
        activationId: 'active', unavailableReason: null, capabilities: [] }] });
    if (route.endsWith('/fixture/details')) return Response.json({ extensionId: 'fixture', packageVersion: '0.5.0',
      manifest: { format: 'nexus.extension.manifest', version: 1, extensionId: 'fixture', displayName: 'Fixture',
        artifactName: 'Fixture', minHostVersion: '0.18.0', packageVersion: '0.5.0', kind: 'data-specialized' },
      documents: {}, diagnosticLocales: {} });
    if (route.endsWith('/editor/open-binding'))
      return Response.json({ session: { sessionId: 'session', revision: 1, phase: 'editing' }, options: [] });
    if (route.endsWith('/session/save-binding'))
      return Response.json({ session: { sessionId: 'session', revision: 2, phase: 'saved' } });
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
  return { store, writes };
}
export function bindingEditorFixture(parent: HTMLElement) {
  const { store, writes } = draftFixture();
  const visible = ref(true);
  const holder = document.createElement('section');
  parent.append(holder);
  const app = createApp({ render: () => visible.value ? h(BindingEditorDialog, {
    store, bindingId: 'binding', bindingRevision: 1, scriptRevision: 1, extensionId: undefined,
    visible: true, onClose: () => { visible.value = false; }
  }) : null });
  app.provide(registryKey, new FrontendRegistry({ locale: 'zh-CN', appearance: 'dark', translate: (_id, key) => key }));
  app.mount(holder);
  return { holder, writes, reopen: () => { visible.value = true; }, dispose: () => { app.unmount(); holder.remove(); store.dispose(); } };
}
export function mountAdapter(component: Component, fixture: ReturnType<typeof draftFixture>, parent: HTMLElement) {
  const holder = document.createElement('section'); parent.append(holder);
  const overview: ConfigurationOverview = { bindingId: 'binding-adapter', bindingRevision: 1, scriptId: 'script',
    scriptRevision: 1, kind: 'specialized', extensionId: 'fixture',
    resources: [{ resourceId: 'resource', declarationIndex: 0, label: 'Saved configuration', revision: 1,
      isSaved: true, shape: 'file', fileCount: 1, relativePath: 'config.json', syntax: 'json' }] };
  const app = createApp({ render: () => h(component, { store: fixture.store, overview, visible: true }) });
  app.component('FormSelect', FormSelect); app.mount(holder);
  return { holder, dispose() { app.unmount(); holder.remove(); } };
}
export async function exitDecision(store: ReturnType<typeof draftFixture>['store']) {
  const result = prepareAppExit(store.client)('close');
  await settle();
  const dialog = document.querySelector('nexus-confirm-dialog');
  return { prompted: !!dialog, accepted: await result === 'ready' };
}
