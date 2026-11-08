import { contractFingerprint, sdkVersion } from '@nexuspipeline/extension-sdk';
import { ControlClient, type Decoder } from '../../apps/web/src/control/client';
import { MediaPool } from '../../apps/web/src/control/media-pool';
import { FrontendLoader } from '../../apps/web/src/extensions/frontend-loader';
import { FrontendRegistry } from '../../apps/web/src/extensions/frontend-registry';

export async function frontendLoadingContract(check: (value: unknown, name: string) => void): Promise<void> {
  const activationId = '11111111-1111-4111-8111-111111111111';
  const entries = ['slow-one', 'slow-two', 'slow-three', 'slow-four', 'slow-five', 'overview-panel', 'slow-six', 'slow-seven'].map(extensionId => ({ activationId, manifest: {
    format: 'nexus.extension.manifest', version: 1, kind: 'managed-code', extensionId,
    sdkVersion, contractFingerprint, capabilities: ['ui.contribute'], frontend: { sdkVersion, styles: [] }
  } }));
  let release!: () => void;
  let active = 0, maximum = 0;
  const waiting = new Promise<void>(resolve => { release = resolve; });
  class Client extends ControlClient {
    override async read<T>(path: string, decode: Decoder<T>): Promise<T> {
      return decode(path.endsWith('/identity') ? { activationId } : { connectionKind: 'local', extensions: entries });
    }
    override async response(path: string): Promise<Response> {
      const id = path.split('/')[4]!;
      active++; maximum = Math.max(maximum, active);
      if (id.startsWith('slow-')) await waiting;
      active--;
      const source = `export function createFrontend() { return {
        extensionId: ${JSON.stringify(id)}, sdkVersion: ${JSON.stringify(sdkVersion)},
        contractFingerprint: ${JSON.stringify(contractFingerprint)}, activate(context) {
          context.registerSurface({id:'panel',target:'overview.cards',dashboard:{key:${JSON.stringify(id)},title:'Example',description:'Example card',defaultVisible:true,order:0},mount(){}});
        }
      }; }`;
      return Response.json({ entry: btoa(source), styles: [] });
    }
  }
  const client = new Client();
  const registry = new FrontendRegistry({ locale: 'zh-CN', appearance: 'dark', translate: (_id, key) => key });
  const failures: unknown[] = [];
  const loader = new FrontendLoader(client, new MediaPool(client), registry, error => failures.push(error));
  let ready!: () => void;
  const displayed = new Promise<void>(resolve => { ready = resolve; });
  const unsubscribe = registry.subscribe(id => { if (id === 'overview-panel') ready(); });
  const work = loader.synchronize();
  let deadline: ReturnType<typeof setTimeout> | undefined;
  try {
    await Promise.race([displayed, new Promise((_, reject) => {
      deadline = setTimeout(() => reject(new Error('overview waited for an unrelated slow extension')), 5000);
    })]);
    check(registry.listSurfaces('overview.cards').some(item => item.extensionId === 'overview-panel'),
      'overview content becomes available while an unrelated extension remains loading');
    release(); await work;
    check(registry.listSurfaces('overview.cards').length === entries.length && !failures.length,
      'all independently loaded extensions publish their validated contributions');
    check(maximum <= 6, 'frontend resource reads retain a bounded concurrency ceiling');
    check(registry.listSurfaces('overview.cards').every(item => item.dashboard?.key === item.extensionId),
      'arbitrary extension identities supply their own persistent dashboard keys');
    await loader.dispose();
    check(registry.listSurfaces('overview.cards').length === 0, 'disposing concurrent extensions retires all contributions');
  } finally { clearTimeout(deadline); release(); unsubscribe(); await work; await loader.dispose(); }
}
