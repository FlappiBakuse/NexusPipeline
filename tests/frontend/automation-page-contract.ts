import { createApp, h } from 'vue';
import { createStore } from '../../apps/web/src/app/store';
import PageActions from '../../apps/web/src/components/PageActions.vue';
import { ControlClient } from '../../apps/web/src/control/client';
import type { AutomationBundle, ControlAutomationChange } from '../../apps/web/src/control/wire.generated';
import AutomationPage from '../../apps/web/src/pages/AutomationPage.vue';
import BindingEditorDialog from '../../apps/web/src/pages/BindingEditorDialog.vue';
import { FrontendRegistry } from '../../apps/web/src/extensions/frontend-registry';
import { registryKey } from '../../apps/web/src/extensions/registry-context';
import { settle } from './configuration-draft-fixture';

type Check = (value: unknown, name: string) => void;
type Store = ReturnType<typeof createStore>;
type Script = AutomationBundle['scripts'][number];
const emptyBundle: AutomationBundle = { format: 'nexus.automation.bundle', version: 1, exportKind: 'definitions',
  bundleId: crypto.randomUUID(), createdAt: new Date().toISOString(), roots: [], scripts: [], users: [], bindings: [],
  queues: [], schedules: [], attachments: [] };

function mountPage(parent: HTMLElement, store: Store, kind: 'users' | 'scripts') {
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
  store.refresh = async () => {
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
    modal.querySelector('nexus-text-input')!.dispatchEvent(new CustomEvent('nexus-change', { detail: { value: 'New account' } }));
    await settle();
    modal.querySelector<HTMLButtonElement>('button[type=submit]')!.click();
    await Promise.race([refreshed.promise, new Promise((_, reject) => setTimeout(() => reject(Error('Account save did not refresh')), 1500))]);
    await settle();
    check(dialogTitle(modal) === '添加用户' && modal.shadowRoot?.querySelector('dialog')?.dataset.size === 'normal' &&
      !modal.querySelector('.bindings-section'),
      'the creation modal keeps its original title and fields when the new user reaches the catalog');
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
    check(dialogTitle(holder.querySelector('nexus-modal')) === '用户管理' && holder.querySelector('.bindings-section'),
      'an explicit edit after creation still opens user management');
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
  store.refresh = async () => {
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
  const requests: string[] = [];
  const store = createStore(new ControlClient(location.origin, async (_url, init) => {
    requests.push(String(init?.body));
    throw new TypeError('transport_lost');
  }));
  store.state.capabilities = { instanceId: 'binding-editor-fixture', hostVersion: '0.18.0',
    capabilities: ['configuration.native.open'] };
  const holder = document.createElement('section');
  parent.append(holder);
  const app = createApp({ render: () => h(BindingEditorDialog, {
    store, bindingId: crypto.randomUUID(), bindingRevision: 1, scriptRevision: 1, extensionId: undefined, visible: true
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
    retry?.click();
    await settle();
    check(requests.length === 2 && requests[0] === requests[1],
      'checking an uncertain editor operation keeps the original request identity');
  } finally { app.unmount(); store.dispose(); holder.remove(); }
}

export async function automationPageContract(parent: HTMLElement, check: Check): Promise<void> {
  const failures: string[] = [];
  const record: Check = (value, name) => { if (value) check(value, name); else failures.push(name); };
  await accountSave(parent, record);
  await scriptBadges(parent, record);
  await accountFailure(parent, record);
  await accountDelete(parent, record);
  await bindingEditorFailure(parent, record);
  if (failures.length) throw new Error(failures.join('\n'));
}
