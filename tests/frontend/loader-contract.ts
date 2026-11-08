import { contractFingerprint, sdkVersion, type FrontendTransport } from '@nexuspipeline/extension-sdk';
import { FrontendLoader } from '../../apps/web/src/extensions/frontend-loader';
import { FrontendRegistry } from '../../apps/web/src/extensions/frontend-registry';
import type { ControlClient } from '../../apps/web/src/control/client';
import type { MediaPool } from '../../apps/web/src/control/media-pool';

export async function loaderContract(check: (value: unknown, name: string) => void): Promise<void> {
  const registry = new FrontendRegistry({ locale: 'en', appearance: 'dark', translate: (_id, key) => key });
  const activationId = '11111111-1111-4111-8111-111111111111';
  const entries = ['fast-fixture', 'slow-fixture'].map(extensionId => ({ activationId, manifest: {
    format: 'nexus.extension.manifest', version: 1, extensionId, artifactName: 'Fixture', displayName: 'Fixture',
    packageVersion: '0.5.0', kind: 'managed-code', minHostVersion: '0.18.0', sdkVersion, contractFingerprint,
    capabilities: ['ui.contribute'], permissions: {}, managed: { entryAssembly: 'Fixture.dll', entryType: 'Fixture.Entry' },
    frontend: { entry: 'web/main.js', styles: [], sdkVersion }
  } }));
  let release!: () => void;
  const transports = new Map<string, FrontendTransport>();
  const state = { wait: new Promise<void>(resolve => { release = resolve; }),
    capture(id: string, transport: FrontendTransport) { transports.set(id, transport); } };
  const global = globalThis as typeof globalThis & { loaderFixture?: typeof state };
  global.loaderFixture = state;
  const downloads: string[] = [];
  const client = {
    read: async (path: string) => path.endsWith('/identity') ? { activationId } : { connectionKind: 'local', extensions: entries },
    response: async (path: string) => {
      const id = path.split('/')[4]!; downloads.push(id);
      const source = `export function createFrontend(transport) { return {
        extensionId: ${JSON.stringify(id)}, sdkVersion: ${JSON.stringify(sdkVersion)}, contractFingerprint: ${JSON.stringify(contractFingerprint)},
        async activate(context) {
          globalThis.loaderFixture.capture(${JSON.stringify(id)}, transport);
          if (${JSON.stringify(id)} === 'slow-fixture') await globalThis.loaderFixture.wait;
          context.registerSurface({id:'card',target:'overview.cards',mount(){}});
        }
      }; }`;
      return Response.json({ entry: btoa(source), styles: [] });
    },
    query: async () => ({ available: true })
  } as unknown as ControlClient;
  const errors: unknown[] = [];
  const loader = new FrontendLoader(client, {} as MediaPool, registry, error => errors.push(error));
  const flush = () => new Promise(resolve => setTimeout(resolve, 0));
  try {
    const first = loader.synchronize();
    for (let attempt = 0; attempt < 100 && (!transports.has('slow-fixture') || !registry.listSurfaces('overview.cards').length); attempt++) await flush();
    check(registry.listSurfaces('overview.cards').length === 1, 'fast plugin activates while unrelated plugin is pending');
    loader.cancelPending();
    state.wait = Promise.resolve();
    const retry = loader.synchronize();
    await Promise.all([first, retry]);
    check(registry.listSurfaces('overview.cards').length === 2, 'retry during cancellation waits for the replacement activation batch');
    const result = await transports.get('fast-fixture')!.invoke('read-state', {}, new AbortController().signal) as { available: boolean };
    check(result.available, 'cancelling pending plugins preserves the completed plugin transport');
    release(); await flush();
    check(registry.listSurfaces('overview.cards').length === 2 && !errors.length, 'late cancelled activation cannot replace the current registrations');
    const downloaded = downloads.length;
    await loader.synchronize();
    check(downloads.length === downloaded && registry.listSurfaces('overview.cards').length === 2,
      'reconciliation reuses active frontend modules and contributions');
    loader.disconnect();
    let revoked = false;
    try { await transports.get('fast-fixture')!.invoke('read-state', {}, new AbortController().signal); } catch { revoked = true; }
    check(revoked, 'disconnect still revokes completed plugin transport');
  } finally { release(); await loader.dispose(); delete global.loaderFixture; }
}
