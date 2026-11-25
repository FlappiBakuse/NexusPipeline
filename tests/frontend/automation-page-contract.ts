import { createApp, h, ref } from 'vue';
import type { UiElement } from '@nexuspipeline/extension-sdk';
import { createStore } from '../../apps/web/src/app/store';
import PageActions from '../../apps/web/src/components/PageActions.vue';
import FormSelect from '../../apps/web/src/components/FormSelect';
import { ControlClient } from '../../apps/web/src/control/client';
import type { AutomationBundle, ControlAutomationChange } from '../../apps/web/src/control/wire.generated';
import AutomationPage from '../../apps/web/src/pages/AutomationPage.vue';
import BindingEditorDialog from '../../apps/web/src/pages/BindingEditorDialog.vue';
import EmulatorFields from '../../apps/web/src/pages/EmulatorFields.vue';
import { FrontendRegistry, registryKey } from '../../apps/web/src/extensions/frontend-registry';
import { clearExtensionIcons, extensionIcon, prepareExtensionIcons } from '../../apps/web/src/control/extension-icons';
import { draftFixture, settle } from './configuration-draft-fixture';

type Check = (value: unknown, name: string) => void;
type Store = ReturnType<typeof createStore>;
type Script = AutomationBundle['scripts'][number];
const emptyBundle: AutomationBundle = { format: 'nexus.automation.bundle', version: 1, exportKind: 'definitions',
  bundleId: crypto.randomUUID(), createdAt: new Date().toISOString(), roots: [], scripts: [], users: [], bindings: [],
  queues: [], schedules: [], attachments: [] };

function mountPage(parent: HTMLElement, store: Store, kind: 'users' | 'scripts' | 'queues') {
  const holder = document.createElement('section');
  const actions = document.createElement('div');
  actions.id = 'page-actions';
  parent.append(actions, holder);
  const app = createApp({ render: () => h(AutomationPage, { store, kind, visible: true }) });
  app.component('PageActions', PageActions);
  app.provide(registryKey, new FrontendRegistry({ locale: 'zh-CN', appearance: 'dark', translate: (_id, key) => key }));
  app.mount(holder);
  return { holder, actions, dispose: () => { app.unmount(); store.dispose(); holder.remove(); actions.remove(); } };
}

async function accountSave(parent: HTMLElement, check: Check) {
  let command: ControlAutomationChange | undefined, writeCount = 0;
  const refreshed = Promise.withResolvers<void>(), finish = Promise.withResolvers<void>();
  const store = createStore(new ControlClient(location.origin, async (url, init) => {
    if (new URL(String(url)).pathname !== '/control/v1/automation/change')
      throw Error('Unexpected account request');
    command = JSON.parse(String(init?.body));
    writeCount++;
    return Response.json({ operationId: command!.operationId, catalogRevision: 2, isNew: true });
  }));
  store.state.capabilities = { instanceId: 'account-save-fixture', hostVersion: '0.18.0', capabilities: ['automation.write'] };
  store.state.automation = { catalogRevision: 1, bundle: emptyBundle, roots: [], revisions: [] };
  store.refreshCatalog = async () => {
    const user = command!.users[0]!.value;
    store.state.automation = { catalogRevision: 2, roots: [], revisions: [{ kind: 'user', id: user.id, revision: 1 }],
      bundle: { ...emptyBundle, users: [user] } };
    refreshed.resolve();
    await finish.promise;
  };
  const { holder, actions, dispose } = mountPage(parent, store, 'users');
  const dialogTitle = (modal?: Element | null) => modal?.shadowRoot?.querySelector('h2')?.textContent;
  try {
    actions.querySelector('button')!.click();
    await settle();
    const modal = holder.querySelector('nexus-modal')!;
    const nameOnly = () => modal.querySelectorAll('.editor-fields nexus-text-input').length === 1 &&
      modal.querySelector('nexus-text-input')?.getAttribute('aria-label') === '用户名' &&
      !modal.querySelector('nexus-text-area, .bindings-section');
    check(nameOnly(), 'creating a user exposes exactly the username field');
    modal.querySelector('nexus-text-input')!.dispatchEvent(new CustomEvent('nexus-change', { detail: { value: 'New account' } }));
    await settle();
    modal.querySelector<HTMLButtonElement>('button[type=submit]')!.click();
    await Promise.race([refreshed.promise, new Promise((_, reject) => setTimeout(() => reject(Error('Account save did not refresh')), 1500))]);
    await settle();
    check(dialogTitle(modal) === '添加用户' && modal.shadowRoot?.querySelector('dialog')?.dataset.size === 'normal' &&
      nameOnly(), 'the creation modal stays username-only when the new user reaches the catalog');
    check(command?.users[0]?.value.name === 'New account' && command.users[0].value.notes === undefined &&
      command.bindings.length === 0, 'adding a user writes the username without notes or bindings');
    finish.resolve();
    await settle();
    await new Promise(resolve => setTimeout(resolve, 230));
    const name = holder.querySelector<HTMLElement>('.entity-card-name')!;
    check(writeCount === 1 && !holder.contains(modal) && name?.textContent === 'New account',
      'the successful save closes the modal and keeps the new user in the list');
    name.click();
    await settle();
    check(name.localName === 'h2' && !holder.querySelector('nexus-modal'), 'the account title is display-only');
    holder.querySelector('.entity-card-actions nexus-icon-button')!.shadowRoot!.querySelector('button')!.click();
    await settle();
    const editor = holder.querySelector('nexus-modal');
    check(dialogTitle(editor) === '用户管理' && editor?.querySelector('nexus-text-area')?.getClientRects().length &&
      editor.querySelector('.bindings-section')?.getClientRects().length,
      'user management exposes both notes and the binding list when editing an existing account');
  } finally { finish.resolve(); dispose(); }
}

async function scriptBadges(parent: HTMLElement, check: Check) {
  const store = createStore(new ControlClient(location.origin, async () => Response.json({ hasReleaseTrust: false, items: [{
    extensionId: 'fixture', installedRevision: 1, isInstalled: true, activationId: 'fixture-active', desiredEnabled: true,
    unavailableReason: null, kind: 'data-specialized', artifactName: 'Test adapter', capabilities: ['task.adapt']
  }] })));
  store.state.capabilities = { instanceId: 'card-badges-fixture', hostVersion: '0.18.0', capabilities: ['extensions.read'] };
  const base: Script = { id: 'generic', name: 'Generic', kind: 'generic', rootId: 'root', executable: { rootId: 'root', relative: 'script.exe' },
    arguments: [], configurationPaths: [], logPaths: [], adapterResources: [], updateConfigurationAtFinish: false,
    limits: { maximumAttempts: 3, stallTimeoutSeconds: 300, totalTimeoutSeconds: 7200 },
    judgement: { language: 'keywords', successKeywords: [], failureKeywords: [] } };
  const target = { launchByHost: true, waitSeconds: 30, forceCloseAtEnd: true };
  const scripts: Script[] = [base, { ...base, id: 'pc', target: { ...target, kind: 'pc' },
    judgement: { ...base.judgement, successKeywords: ['done'] }, limits: { ...base.limits, stallTimeoutSeconds: 0 } },
    { ...base, id: 'android', target: { ...target, kind: 'android' },
      judgement: { ...base.judgement, language: 'javascript', script: 'function nexusJudge() {}' } },
    { ...base, id: 'specialized', kind: 'specialized', extensionId: 'fixture', target: { ...target, kind: 'pc', launchByHost: false } },
    { ...base, id: 'missing', kind: 'specialized', extensionId: 'missing-fixture' }];
  store.state.automation = { catalogRevision: 1, bundle: { ...emptyBundle, scripts }, roots: [], revisions: [] };
  const { holder, dispose } = mountPage(parent, store, 'scripts');
  try {
    await settle();
    const labels = (id: string) => [...holder.querySelectorAll(`[data-item-id="${id}"] nexus-badge`)]
      .map(badge => badge.shadowRoot?.querySelector('span')?.textContent).join('|');
    check(labels('generic') === '通用脚本' && labels('pc') === '通用脚本|PC 客户端|关键字判断|长时策略' &&
      labels('android') === '通用脚本|安卓模拟器|判定脚本', 'script cards display the matching game, judgement and long-running badges');
    check(labels('specialized') === 'Test adapter 专项' && labels('missing') === '未知专项',
      'the script library shares the plugin catalog and displays its name or an unavailable badge');
  } finally { dispose(); }
}

async function accountFailure(parent: HTMLElement, check: Check) {
  let response = Promise.withResolvers<Response>();
  const store = createStore(new ControlClient(location.origin, () => response.promise));
  store.state.capabilities = { instanceId: 'account-feedback-fixture', hostVersion: '0.18.0', capabilities: ['automation.write'] };
  store.state.automation = { catalogRevision: 1, bundle: emptyBundle, roots: [], revisions: [] };
  const { holder, actions, dispose } = mountPage(parent, store, 'users');
  const feedback = () => [...holder.querySelectorAll<HTMLElement>('nexus-toast:popover-open')];
  try {
    actions.querySelector('button')!.click();
    await settle();
    const modal = holder.querySelector('nexus-modal')!;
    modal.querySelector('nexus-text-input')!.dispatchEvent(new CustomEvent('nexus-change', { detail: { value: 'Retained account' } }));
    await settle();
    const save = modal.querySelector<HTMLButtonElement>('button[type=submit]')!;
    save.click();
    await settle();
    check(!feedback().length, 'saving an account does not announce an unknown outcome while the write is in flight');
    response.resolve(Response.json({ code: 'fixture_rejected' }, { status: 409 }));
    await settle();
    check(feedback().length === 1 && feedback()[0]?.dataset.tone === 'danger' && !store.state.error,
      'a known account rejection is reported only by its editor');
    response = Promise.withResolvers<Response>();
    save.click();
    await settle();
    response.reject(new TypeError('transport_lost'));
    await settle();
    check(feedback().length === 1 && feedback()[0]?.dataset.tone === 'warning' && !store.state.uncertainOperation &&
      [...modal.querySelectorAll('button')].some(button => button.textContent?.includes('查询操作状态')),
      'a lost account response yields one warning and keeps its original operation available for inspection');
  } finally { dispose(); }
}

async function queueAvatar(parent: HTMLElement, check: Check) {
  const pixel = await new Promise<Blob>(resolve => document.createElement('canvas').toBlob(blob => resolve(blob!)));
  const client = new ControlClient(location.origin, async () => new Response(pixel));
  const store = createStore(client);
  const firstId = 'script-first', secondId = 'script-second';
  const scripts = [{ id: firstId, name: 'First', extensionId: 'baah' },
    { id: secondId, name: 'Second', extensionId: 'bettergi' }] as AutomationBundle['scripts'];
  const queue: AutomationBundle['queues'][number] = { id: 'queue', name: 'Queue', scriptIds: [firstId, secondId],
    order: 0, completionAction: 'none', notifyEnabled: false, notificationTaskIds: [] };
  const publish = (scriptIds: string[]) => {
    store.state.automation = { catalogRevision: 1, bundle: { ...emptyBundle, scripts, queues: [{ ...queue, scriptIds }] }, roots: [], revisions: [] };
  };
  publish([firstId, secondId]);
  const { holder, dispose } = mountPage(parent, store, 'queues');
  try {
    await prepareExtensionIcons(client, ['baah', 'bettergi'].map(extensionId => ({ extensionId, installedRevision: 1 })),
      new AbortController().signal); await settle();
    const avatar = () => holder.querySelector<HTMLImageElement>('.automation-row .script-avatar img');
    check(avatar()?.getAttribute('src') === extensionIcon('baah'), 'a queue shows the icon of its first script instance');
    publish([secondId, firstId]); await settle();
    check(avatar()?.getAttribute('src') === extensionIcon('bettergi'), 'reordering the queue changes its icon to its new first script');
    publish([]); await settle();
    check(!avatar() && holder.querySelector<UiElement<'nexus-icon'>>('.script-avatar nexus-icon')?.name === 'queues',
      'an empty queue retains its queue icon');
  } finally { dispose(); clearExtensionIcons(); }
}

async function accountDelete(parent: HTMLElement, check: Check) {
  let command: ControlAutomationChange | undefined;
  const store = createStore(new ControlClient(location.origin, async (_url, init) => {
    command = JSON.parse(String(init?.body));
    return Response.json({ operationId: command!.operationId, catalogRevision: 2, isNew: true });
  }));
  const account = { id: 'test-account', name: '测试 Account', order: 0 };
  store.state.capabilities = { instanceId: 'account-delete-fixture', hostVersion: '0.18.0', capabilities: ['automation.delete'] };
  store.state.automation = { catalogRevision: 1, bundle: { ...emptyBundle, users: [account] }, roots: [],
    revisions: [{ kind: 'user', id: account.id, revision: 4 }] };
  store.refreshCatalog = async () => {
    store.state.automation = { ...store.state.automation!, catalogRevision: 2, bundle: emptyBundle, revisions: [] };
  };
  const { holder, dispose } = mountPage(parent, store, 'users');
  try {
    holder.querySelector('.danger-action')!.shadowRoot!.querySelector('button')!.click();
    await settle();
    const modal = holder.querySelector('nexus-modal')!;
    const input = modal.querySelector('input')!;
    const button = modal.querySelector<HTMLButtonElement>('button.danger')!;
    const enter = async (value: string) => {
      input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })); await settle();
    };
    check(modal.shadowRoot?.querySelector('h2')?.textContent === '删除用户' &&
      modal.querySelector('p')?.textContent?.includes('全部脚本绑定'), 'account deletion describes the exact target and related data');
    for (const value of ['', '测试', '测试 account', account.name + ' ']) {
      await enter(value);
      button.click();
      check(button.disabled && !command, 'a blank, partial, or inexact username cannot submit account deletion');
    }
    await enter(account.name);
    check(!button.disabled, 'the exact saved username enables the explicit delete action');
    button.click();
    await settle();
    check(command?.removed.length === 1 && command.removed[0]?.id === account.id &&
      command.removed[0]?.expectedRevision === 4 && !holder.querySelector('.automation-row'),
      'the matching username submits deletion for the original account revision');
  } finally { dispose(); }
}

async function bindingEditorFailure(parent: HTMLElement, check: Check) {
  const requests: { path: string; body: string }[] = [];
  const bindingId = crypto.randomUUID(), userId = crypto.randomUUID(), scriptId = crypto.randomUUID();
  const candidateId = crypto.randomUUID();
  const revision = ref(1), candidate = ref(candidateId);
  let sessionId = '', launches = 0;
  const store = createStore(new ControlClient(location.origin, async (url, init) => {
    const path = new URL(String(url)).pathname, body = String(init?.body);
    requests.push({ path, body });
    if (path.endsWith('/cancel')) return Response.json({ sessionId, revision: 8, phase: 'cancelled' });
    if (requests.length === 1) {
      sessionId = JSON.parse(body).sessionId;
      launches++;
      throw new TypeError('transport_lost');
    }
    if (path !== requests[0]!.path || body !== requests[0]!.body)
      return Response.json({ code: 'operation_conflict' }, { status: 409 });
    return Response.json({ session: { sessionId, revision: 7, phase: 'editing' }, options: [] });
  }));
  store.state.capabilities = { instanceId: 'binding-editor-fixture', hostVersion: '0.18.0',
    capabilities: ['configuration.native.open'] };
  const holder = document.createElement('section');
  parent.append(holder);
  const app = createApp({ render: () => h(BindingEditorDialog, {
    store, bindingId, bindingRevision: 0, scriptRevision: revision.value, extensionId: undefined, visible: true,
    draftBinding: { userId, scriptId, scriptRevision: revision.value, candidateId: candidate.value }
  }) });
  app.provide(registryKey, new FrontendRegistry({ locale: 'zh-CN', appearance: 'dark', translate: (_id, key) => key }));
  app.mount(holder);
  try {
    await settle();
    const feedback = holder.querySelectorAll('nexus-toast:popover-open');
    const retry = [...holder.querySelectorAll('button')].find(button => button.textContent?.includes('查询操作状态'));
    check(feedback.length === 1 && (feedback[0] as HTMLElement)?.dataset.tone === 'warning' &&
      !holder.querySelector('p[role=status]') && !!retry,
      'an uncertain editor open presents one warning toast and keeps the query action');
    revision.value = 2;
    candidate.value = crypto.randomUUID();
    await settle();
    retry?.click();
    await settle();
    check(requests.length === 2 && requests[1]?.path === requests[0]?.path && requests[1]?.body === requests[0]?.body &&
      launches === 1 && !!sessionId && JSON.parse(requests[0]!.body).draftCandidateId === candidateId,
      'an accepted editor open replays the same route and bytes after parent revision and draft changes');
    const cancel = [...holder.querySelectorAll('button')].find(button => button.textContent?.trim() === '取消');
    cancel?.click();
    await settle();
    check(requests[2]?.path.endsWith('/' + sessionId + '/cancel') && JSON.parse(requests[2]!.body).expectedSessionRevision === 7,
      'the replay attaches the original result and its revision to the editor');
  } finally { app.unmount(); store.dispose(); holder.remove(); }
}

async function activeBindingRemoval(parent: HTMLElement, check: Check) {
  const { store, writes } = draftFixture(route => {
    if (route.endsWith('/session/cancel')) return Response.json({ sessionId: 'session', revision: 2, phase: 'cancelled' });
    if (route.endsWith('/session/save-binding')) return Response.json({
      session: { sessionId: 'session', revision: 2, phase: 'saved' }, draftCandidate: 'prepared-configuration'
    });
    if (route.endsWith('/prepared-configuration/discard')) return Response.json({});
  });
  store.state.capabilities!.capabilities.push('automation.write');
  const script = { id: 'script', name: 'Native fixture', kind: 'specialized', extensionId: 'fixture' } as Script;
  const user = { id: crypto.randomUUID(), name: 'Existing account', order: 0 };
  store.state.automation = {
    ...store.state.automation!,
    revisions: [{ kind: 'script', id: script.id, revision: 1 }, { kind: 'user', id: user.id, revision: 1 }],
    bundle: { ...emptyBundle, scripts: [script], users: [user] }
  };
  const { holder, dispose } = mountPage(parent, store, 'users');
  const press = (root: Element, text: string) => {
    [...root.querySelectorAll('button')].find(button => button.textContent?.trim() === text)!.click();
  };
  try {
    holder.querySelector('.entity-card-actions nexus-icon-button')!.shadowRoot!.querySelector('button')!.click();
    await settle();
    const account = holder.querySelector('nexus-modal')!;
    account.querySelector<HTMLButtonElement>('.add-binding')!.click();
    await settle();
    press(account, 'Native fixture');
    await settle();
    press(account, '确定');
    await settle();
    const toggle = account.querySelector<HTMLButtonElement>('.binding-toggle')!;
    toggle.click();
    await settle();
    account.querySelector<HTMLButtonElement>('.edit-configuration')!.click();
    await settle();
    const editor = [...holder.querySelectorAll('nexus-modal')].find(modal => modal !== account)!;
    press(editor, '关闭');
    await settle();
    toggle.click();
    await settle();
    press(account, '编辑绑定');
    await settle();
    press(account, '移除绑定');
    await settle();
    const retained = account.contains(toggle);
    check(retained, 'removing an unsaved binding keeps its active native configuration session reachable');
    if (!retained) return;
    press(account, '完成编辑');
    await settle();
    toggle.click();
    await settle();
    account.querySelector<HTMLButtonElement>('.edit-configuration')!.click();
    await settle();
    const resumed = [...holder.querySelectorAll('nexus-modal')].find(modal => modal !== account)!;
    press(resumed, '取消');
    await settle();
    check(writes.filter(route => route.endsWith('/editor/open-binding')).length === 1 &&
      writes.some(route => route.endsWith('/session/cancel')), 'reopening the retained binding can cancel the original session');
    account.querySelector<HTMLButtonElement>('.edit-configuration')!.click();
    await settle();
    const staged = [...holder.querySelectorAll('nexus-modal')].find(modal => modal !== account)!;
    press(staged, '保留到草稿');
    await settle();
    const bindingId = toggle.closest<HTMLElement>('[data-item-id]')!.dataset.itemId!;
    const managed = store.editors.target(store.state.capabilities!.instanceId, 'bindings', bindingId)
      .child('extension/maa-framework').child('automation.binding.editor');
    const release = managed.guardCancel(() => false);
    press(account, '放弃配置草稿');
    await settle();
    check(!managed.signal.aborted && !writes.some(route => route.endsWith('/prepared-configuration/discard')),
      'discarding a staged draft waits for the managed editor to settle');
    release();
    press(account, '放弃配置草稿');
    await settle();
    check(account.contains(toggle) && managed.signal.aborted &&
      writes.some(route => route.endsWith('/prepared-configuration/discard')),
      'discarding a staged configuration keeps the binding and releases its candidate and managed editor state');
    toggle.click();
    await settle();
    press(account, '编辑绑定');
    await settle();
    press(account, '移除绑定');
    await settle();
    check(!account.contains(toggle), 'removing the binding succeeds after the configuration session is cancelled');
  } finally { dispose(); }
}

async function emulatorDiscovery(parent: HTMLElement, check: Check): Promise<void> {
  const rootId = crypto.randomUUID();
  const script = { id: crypto.randomUUID(), rootId } as Script;
  let respond: ((response: Response) => void) | undefined, owner: AbortSignal | undefined;
  let reads = 0;
  const targets = (name: string) => Response.json({ catalogRevision: 2,
    roots: [{ id: rootId, path: 'D:/fixture', name: 'Root', isDeclared: true }],
    targets: [{ name, endpoint: '127.0.0.1:5555', adb: { rootId, relativePath: 'adb.exe' },
      selection: { vendor: 'fixture', rootId, targetId: 'a'.repeat(64), generation: 'b'.repeat(64) } }],
    diagnostics: [{ code: name }] });
  const client = new ControlClient(location.origin, async (_url, init) => {
    reads++;
    if (reads === 1 || reads === 3) {
      owner = init?.signal ?? undefined;
      return new Promise(resolve => { respond = resolve; });
    }
    return targets(reads === 2 ? 'current_discovery' : 'current_generation');
  });
  const store = createStore(client);
  store.state.capabilities = { instanceId: 'original', hostVersion: '0.18.0', capabilities: ['automation.read'] };
  store.state.automation = { catalogRevision: 1, roots: [], revisions: [],
    bundle: { ...emptyBundle, scripts: [script], roots: [{ id: rootId, kind: 'script', name: 'Root' }] } };
  const holder = document.createElement('section'); parent.append(holder);
  const app = createApp({ render: () => h(EmulatorFields, { store, modelValue: script }) }).component('FormSelect', FormSelect);
  app.mount(holder);
  try {
    const discover = holder.querySelector('button')!;
    discover.click(); await settle();
    check(!!respond && !!owner && discover.disabled, 'emulator discovery retains its pending read');
    store.state.automation = { ...store.state.automation, catalogRevision: 2 };
    store.state.capabilities = { ...store.state.capabilities, instanceId: 'replacement' };
    await settle();
    check(owner?.aborted && !discover.disabled, 'switching emulator scope cancels the prior discovery');
    discover.click();
    respond?.(Response.json({ code: 'obsolete_discovery' }, { status: 500 }));
    await settle();
    check(!store.state.error && holder.querySelector<UiElement<'nexus-toast'>>('nexus-toast')?.message?.includes('current_discovery'),
      'a late emulator failure cannot obscure the current discovery diagnostic');
    const choices = () => holder.querySelectorAll<UiElement<'nexus-select'>>('nexus-select')[1]!.options;
    check(choices().some(item => item.label.includes('current_discovery')), 'emulator discovery makes its instance selectable');
    discover.click(); await settle();
    store.state.events = { extensionGeneration: 'new-provider' };
    await settle();
    check(owner?.aborted && choices().length === 1 && !discover.disabled,
      'changing only the emulator provider generation cancels its pending read and removes old options');
    discover.click();
    respond?.(targets('obsolete_generation'));
    await settle();
    check(reads === 4 && choices().some(item => item.label.includes('current_generation')) &&
      !choices().some(item => item.label.includes('obsolete_generation')),
      'a late successful reply can only publish options from the current emulator provider generation');
  } finally { app.unmount(); store.dispose(); holder.remove(); }
}

async function scriptCreation(parent: HTMLElement, check: Check): Promise<void> {
  const png = await new Promise<Blob>(resolve => document.createElement('canvas').toBlob(blob => resolve(blob!)));
  for (const kind of ['none', 'data-specialized', 'managed-code'] as const) {
    const plugin = (extensionId: string, pluginKind: string, capabilities: string[]) => ({
      extensionId, isInstalled: true, installedRevision: 1, desiredEnabled: true, activationId: 'fixture-active',
      unavailableReason: null, kind: pluginKind, artifactName: extensionId, capabilities
    });
    const other = plugin('live-screenshot', 'managed-code', []);
    const types = kind === 'none' ? [other] : [other, kind === 'managed-code'
      ? plugin('maa-framework', kind, ['task.adapt', 'source.compile', 'execution.drive'])
      : plugin('fixture-adapter', kind, ['task.adapt'])];
    const pendingTypes = Promise.withResolvers<void>();
    const pendingIcons = Promise.withResolvers<void>();
    const store = createStore(new ControlClient(location.origin, async input => {
      if (new URL(String(input)).pathname.endsWith('/icon')) {
        if (kind === 'data-specialized') await pendingIcons.promise;
        return new Response(png, { headers: { 'Content-Type': 'image/png' } });
      }
      if (kind === 'none') await pendingTypes.promise;
      return Response.json({ hasReleaseTrust: false, items: types });
    }));
    store.state.capabilities = { instanceId: 'script-creation-' + kind, hostVersion: '0.18.0',
      capabilities: ['automation.write', 'automation.declare', 'extensions.read'] };
    store.state.automation = { catalogRevision: 1, roots: [], revisions: [], bundle: emptyBundle };
    const { holder, actions, dispose } = mountPage(parent, store, 'scripts');
    try {
      await settle();
      actions.querySelector('button')!.click();
      await settle();
      const grid = holder.querySelector<HTMLElement>('.script-types')!;
      const chooser = grid.closest('nexus-modal');
      const editor = () => holder.querySelector('#automation-editor-scripts');
      if (kind === 'none') {
        check(!chooser?.shadowRoot?.querySelector('dialog')?.open && !editor(),
          'the type chooser stays closed while the installed plugin list is pending');
        pendingTypes.resolve();
        await settle();
        check(!!editor() && !chooser?.shadowRoot?.querySelector('dialog')?.open,
          'unrelated installed plugins leave the generic editor as the direct create entry');
        continue;
      }
      if (kind === 'data-specialized') {
        check(!chooser?.shadowRoot?.querySelector('dialog')?.open, 'the picker waits for its installed plugin icons');
        pendingIcons.resolve();
      }
      await new Promise<void>((resolve, reject) => {
        const limit = Date.now() + 3000;
        const timer = setInterval(() => {
          if (!chooser?.shadowRoot?.querySelector('dialog')?.open && Date.now() < limit) return;
          clearInterval(timer);
          if (chooser?.shadowRoot?.querySelector('dialog')?.open) resolve();
          else reject(new Error('The script type picker did not open'));
        }, 20);
      });
      const cards = grid.querySelectorAll<HTMLButtonElement>('.type-choice');
      check(cards[1]?.querySelector('img')?.src.startsWith('blob:'), 'the picker first shows the plugin icon: ' + kind);
      const width = grid.getBoundingClientRect().width;
      const genericWidth = cards[0]?.getBoundingClientRect().width ?? 0;
      check(cards.length === 2 && chooser?.shadowRoot?.querySelector('dialog')?.open && !editor(),
        'installed script-capable plugins keep the type chooser: ' + kind);
      check(width > 0 && (kind === 'managed-code' ? genericWidth < width * 0.6 : Math.abs(genericWidth - width) < 1),
        'generic script card width follows the visible driver choice: ' + kind);
      cards[0]?.click();
      await settle();
      check(!!editor(), 'choosing the generic card opens the same editor: ' + kind);
    } finally { pendingTypes.resolve(); pendingIcons.resolve(); dispose(); clearExtensionIcons(); }
  }
}

export async function automationPageContract(parent: HTMLElement, check: Check): Promise<void> {
  await queueAvatar(parent, check);
  const failures: string[] = [];
  const record: Check = (value, name) => { if (value) check(value, name); else failures.push(name); };
  await accountSave(parent, record);
  await scriptBadges(parent, record);
  await scriptCreation(parent, record);
  await accountFailure(parent, record);
  await accountDelete(parent, record);
  await bindingEditorFailure(parent, record);
  await emulatorDiscovery(parent, record);
  await activeBindingRemoval(parent, record);
  if (failures.length) throw new Error(failures.join('\n'));
}
