import { createApp, h, nextTick, ref } from 'vue';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient } from '../../apps/web/src/control/client';
import { initialValue, schemaAt } from '../../apps/web/src/control/schema';
import ScriptSourceFields from '../../apps/web/src/pages/ScriptSourceFields.vue';
import { scriptPaths } from '../../apps/web/src/pages/script-paths';
import type { AutomationBundle } from '../../apps/web/src/control/wire.generated';
import type { UiElement } from '@nexuspipeline/extension-sdk';

type Script = AutomationBundle['scripts'][number];
const candidate = { candidateId: 'first', title: 'First', executable: 'app.exe', arguments: ['daily'],
  resources: [{ resourceId: 'main', path: 'config.json', syntax: 'json' }], logPaths: ['logs/*.log'], projectSelection: { id: 'first' } };

export async function scriptSourceContract(): Promise<string[]> {
  const checks: string[] = [];
  for (const mode of ['ready', 'readonly', 'empty', 'diagnostics-only', 'details-failure', 'cancelled'] as const) {
    const calls: string[] = [], emitted: Script[] = [];
    let queried = false, release: (() => void) | undefined;
    const client = new ControlClient(location.origin, async (input, init) => {
      const path = new URL(String(input)).pathname; calls.push(path);
      if (path === '/control/v1/extensions') return json({ hasReleaseTrust: true, items: [{ extensionId: 'maaend',
        installedRevision: 3, isInstalled: true, desiredEnabled: true, activationId: 'active', unavailableReason: null, capabilities: [] }] });
      if (path.endsWith('/details')) return mode === 'diagnostics-only' ? json({ extensionId: 'maaend',
        packageVersion: '1.0.0', documents: {}, diagnosticLocales: { 'zh-CN': { fixture_diagnostic: '未找到可用配置文件。' } },
        manifest: { extensionId: 'maaend', format: 'nexus.extension.manifest',
          version: 1, displayName: 'MaaEnd', artifactName: 'MaaEnd', minHostVersion: '0.18.0', packageVersion: '1.0.0',
          kind: 'data-specialized' } }) : json({ code: 'fixture_details_rejected' }, 409);
      if (!path.endsWith('/discover')) throw Error('Unexpected source request: ' + path);
      const request = JSON.parse(String(init!.body)); queried = true;
      if (mode === 'cancelled') await new Promise<void>(resolve => { release = resolve; });
      return json({ rootPath: request.rootPath, installedRevision: 3, discovery: {
        format: 'nexus.script.source', version: 1, direction: 'response', operationId: request.operationId, extensionId: 'maaend',
        candidates: mode === 'empty' || mode === 'diagnostics-only' ? [] : [candidate, { ...candidate, candidateId: 'second', executable: 'second.exe' }],
        diagnostics: mode === 'details-failure' || mode === 'diagnostics-only' ? ['fixture_diagnostic'] : [] } });
    });
    const store = createStore(client);
    store.state.capabilities = { instanceId: 'source-fixture', hostVersion: '0.18.0',
      capabilities: mode === 'readonly' ? ['configuration.read'] : ['configuration.read', 'automation.declare'] };
    const script = ref(initialValue(schemaAt('automation-script.schema.json#/$defs/script')) as Script);
    script.value.id = crypto.randomUUID(); script.value.extensionId = 'maaend'; script.value.kind = 'specialized';
    const paths = scriptPaths(store, script.value); paths.root = 'D:/source-fixture'; paths.executable = '';
    const mount = document.createElement('div'); document.body.append(mount);
    const app = createApp({ render: () => h(ScriptSourceFields, { store, modelValue: script.value,
      'onUpdate:modelValue': (value: Script) => { emitted.push(value); script.value = value; } }) });
    let unmounted = false;
    try {
      app.mount(mount);
      await until(() => queried);
      if (mode === 'cancelled') { app.unmount(); unmounted = true; release!(); }
      await until(() => mode === 'cancelled' || (mode === 'ready' ? emitted.length > 0
        : mode === 'readonly' ? calls.length >= 2 : !!mount.querySelector('nexus-toast')));
      await nextTick();
      if (mode === 'ready') {
        check(emitted.length === 1 && paths.executable === 'app.exe' && paths.configuration === 'config.json',
          'source discovery selects the first candidate once and preserves all source paths', checks);
        check(!calls.some(path => path.endsWith('/details')) && !mount.querySelector('nexus-toast'),
          'successful discovery needs no details request or hidden selection prompt', checks);
      } else if (mode === 'details-failure') {
        check(calls.some(path => path.endsWith('/details')) && !emitted.length && !paths.executable,
          'diagnostic details failure retains its error gate and does not publish a candidate', checks);
      } else if (mode === 'empty') {
        check(!emitted.length && !!mount.querySelector('nexus-toast') && !paths.executable,
          'empty discovery retains its warning without publishing an invalid selection', checks);
      } else if (mode === 'diagnostics-only') {
        check(!emitted.length && mount.querySelector<UiElement<'nexus-toast'>>('nexus-toast')?.message === '未找到可用配置文件。',
          'empty discovery reports the verified diagnostic once without guessing a candidate', checks);
      } else {
        await new Promise(resolve => setTimeout(resolve, 30));
        check(!emitted.length && !paths.executable,
          mode + ' discovery cannot change paths or publish a late selection', checks);
      }
    } finally { if (!unmounted) app.unmount(); store.dispose(); mount.remove(); }
  }
  return checks;
}

function json(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}
function check(value: unknown, name: string, checks: string[]): void {
  if (!value) throw Error(name); checks.push(name);
}
async function until(condition: () => boolean): Promise<void> {
  const deadline = performance.now() + 3000;
  while (!condition()) {
    if (performance.now() > deadline) throw Error('Source fixture timed out');
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}
