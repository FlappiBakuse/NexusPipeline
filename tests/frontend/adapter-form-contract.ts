import { createApp, h, nextTick } from 'vue';
import FormSelect from '../../apps/web/src/components/FormSelect';
import AdapterEditorPanel from '../../apps/web/src/pages/AdapterEditorPanel.vue';
import { ControlClient } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';
import type { ConfigurationOverview } from '../../apps/web/src/pages/configuration-types';

export async function adapterFormContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const operationId = '00000000-0000-4000-8000-000000000001';
  let reads = 0, saves = 0;
  const send: typeof fetch = async (input) => {
    const url = new URL(String(input));
    if (url.pathname === '/control/v1/operations/' + operationId) {
      reads++;
      return Response.json({ operationId, kind: 'configuration.save', status: 'completed' });
    }
    return Response.json({ code: 'configuration_fixture_unavailable' }, { status: 403 });
  };
  const store = createStore(new ControlClient(location.origin, send));
  const overview: ConfigurationOverview = { bindingId: 'binding', bindingRevision: 1, scriptId: 'script', scriptRevision: 1,
    kind: 'specialized', extensionId: 'fixture-adapter', resources: [] };
  const draft = { resourceIds: [], operationId, proposalDigest: '', candidateIndex: -1, previewed: false,
    pending: operationId, pendingAction: 'applyRepair', candidates: [], tasks: [], diagnostics: [], request: null, busy: false, note: '' };
  store.drafts.set('configuration/adapter/undefined/binding', draft);
  const mount = document.createElement('section'); fixture.append(mount);
  const app = createApp({ render: () => h('form', { onSubmit: (event: SubmitEvent) => { event.preventDefault(); saves++; } }, [
    h(AdapterEditorPanel, { store, overview, visible: true }),
    h('button', { type: 'submit' }, 'Save parent')
  ]) });
  app.component('FormSelect', FormSelect);
  app.mount(mount);
  try {
    await nextTick();
    const query = [...mount.querySelectorAll('button')].find(button => button.textContent?.trim() === '查询操作状态');
    check(query && !query.disabled, 'the actual adapter editor exposes its pending-operation query inside a form');
    query!.click();
    for (let attempt = 0; attempt < 50 && !mount.textContent?.includes('修复已应用。'); attempt++) await new Promise(resolve => setTimeout(resolve, 10));
    await nextTick();
    check(reads > 0 && mount.textContent?.includes('修复已应用。') && saves === 0,
      'querying the completed adapter operation shows its result without submitting the parent form');
    const queried = reads;
    mount.querySelector<HTMLButtonElement>('button[type=submit]')!.click();
    check(saves === 1 && reads === queried, 'the explicit parent save submits exactly once after the query');
  } finally { app.unmount(); mount.remove(); store.dispose(); }
}
