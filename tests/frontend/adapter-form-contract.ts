import { createApp, h, nextTick, reactive } from 'vue';
import AdapterEditorPanel from '../../apps/web/src/pages/AdapterEditorPanel.vue';
import { ControlClient } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';
import type { ConfigurationOverview } from '../../apps/web/src/pages/adapter-contracts';
import { configurationDrafts } from '../../apps/web/src/pages/configuration-drafts';
import { draftFixture, settle } from './configuration-draft-fixture';

export async function adapterFormContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  await unknownRepair(fixture, check);
  await repairCandidates(fixture, check);
}

async function unknownRepair(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
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
  store.state.capabilities = { instanceId: 'fixture', hostVersion: '0.18.0', capabilities: ['operations.read'] };
  const bindingId = 'binding';
  const draft = { pending: operationId, busy: false, note: '', error: '' };
  configurationDrafts(store, bindingId).child('adapter').retain('state', () => reactive(draft));
  const mount = document.createElement('section'); fixture.append(mount);
  const app = createApp({ render: () => h('form', { onSubmit: (event: SubmitEvent) => { event.preventDefault(); saves++; } }, [
    h(AdapterEditorPanel, { store, bindingId, visible: true }),
    h('button', { type: 'submit' }, 'Save parent')
  ]) });
  app.mount(mount);
  try {
    await nextTick();
    const query = [...mount.querySelectorAll('button')].find(button => button.textContent?.trim() === '查询操作状态');
    check(query && !query.disabled, 'the actual adapter editor exposes its pending-operation query inside a form');
    const pending = [...mount.querySelectorAll<HTMLElement>('nexus-toast')].find(toast => toast.getAttribute('data-tone') === 'warning' &&
      toast.shadowRoot?.textContent?.includes('修复操作待确认'));
    check(pending?.matches(':popover-open') && mount.querySelectorAll('nexus-toast:popover-open').length === 1,
      'the uncertain adapter operation is announced once even when the adapter is unavailable');
    pending!.hidePopover();
    check(query!.isConnected && !query!.disabled, 'dismissing the feedback keeps the original operation query available');
    query!.click();
    const success = () => [...mount.querySelectorAll('nexus-toast:popover-open')]
      .some(toast => toast.shadowRoot?.querySelector('span')?.textContent === '修复已应用。');
    for (let attempt = 0; attempt < 50 && !success(); attempt++) await new Promise(resolve => setTimeout(resolve, 10));
    await nextTick();
    check(reads > 0 && success() && saves === 0,
      'querying the completed adapter operation shows its result without submitting the parent form');
    const queried = reads;
    mount.querySelector<HTMLButtonElement>('button[type=submit]')!.click();
    check(saves === 1 && reads === queried, 'the explicit parent save submits exactly once after the query');
  } finally { app.unmount(); mount.remove(); store.dispose(); }
}

async function repairCandidates(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const proposedDigest = 'f'.repeat(64);
  let proposedId = '', appliedId = '', appliedIndex = -1, appliedDigest = '', saves = 0;
  const responses = draftFixture((route, options) => {
    if (!route.startsWith('/control/v1/adapters/')) return;
    const { action, selection, candidateIndex, proposalDigest } = JSON.parse(String(options?.body));
    const source = { bindingId: 'binding', bindingRevision: 2, scriptRevision: 1, extensionId: 'fixture',
      installedRevision: 1, resources: [{ resourceId: 'main', revision: 3, relativePath: 'main.json' }] };
    if (action === 'propose') {
      proposedId = selection.operationId;
      return Response.json({ source, diagnostics: [], proposalDigest: proposedDigest,
        candidates: [{ index: 0, resourceId: 'main', editCount: 1 }, { index: 1, resourceId: 'main', editCount: 2 },
          { index: 2, resourceId: 'main', editCount: 1 }] });
    }
    if (action === 'previewRepair')
      return Response.json({ source, candidateIndex, proposalDigest, hasChanges: candidateIndex !== 2 });
    appliedId = selection.operationId;
    appliedIndex = candidateIndex;
    appliedDigest = proposalDigest;
    return Response.json({ operationId: appliedId, bindingId: 'binding', resourceId: 'main', revision: 4, hasChanges: true });
  });
  const overview: ConfigurationOverview = { bindingId: 'binding', bindingRevision: 2, scriptId: 'script', scriptRevision: 1,
    kind: 'specialized', extensionId: 'fixture', resources: [{ resourceId: 'main', declarationIndex: 0, label: 'Main configuration',
      revision: 3, isSaved: true, shape: 'file', fileCount: 1, relativePath: 'main.json', syntax: 'json' }] };
  const section = document.createElement('section'); fixture.append(section);
  const source = { overview, installedRevision: 1 };
  const app = createApp({ render: () => h('form', { onSubmit: (event: SubmitEvent) => { event.preventDefault(); saves++; } },
    [h(AdapterEditorPanel, { store: responses.store, bindingId: 'binding', visible: true, source })]) });
  app.mount(section);
  const button = (label: string) => [...section.querySelectorAll('button')].find(item => item.textContent?.trim() === label)!;
  const changed = (id: string) => section.querySelector('nexus-select')!.dispatchEvent(new CustomEvent('nexus-change', { detail: { value: id } }));
  try {
    button('查找修复候选').click(); await settle();
    check(!!section.querySelector('nexus-select') && !!proposedId && button('应用修复').disabled && saves === 0,
      'finding complete repair candidates creates no parent save and keeps apply gated');
    button('预览修复').click(); await settle();
    check(!button('应用修复').disabled, 'a bound preview enables the chosen candidate');
    changed('2'); await settle();
    check(button('应用修复').disabled && !section.textContent?.includes('修复预览已完成'),
      'selecting another candidate invalidates the original preview');
    button('预览修复').click(); await settle();
    check(button('应用修复').disabled && !!section.textContent?.includes('无实际更改'), 'an unchanged candidate displays its preview without enabling apply');
    changed('1'); await settle();
    button('预览修复').click(); await settle();
    button('应用修复').click(); await settle();
    check(appliedId === proposedId && appliedIndex === 1 && appliedDigest === proposedDigest && saves === 0,
      'explicit apply submits only the previewed proposal identity, digest and candidate without submitting the parent form');
  } finally { app.unmount(); section.remove(); responses.store.dispose(); }
}
