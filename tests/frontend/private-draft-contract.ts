import { createApp, h, ref } from 'vue';
import NotificationEditor from '../../apps/web/src/pages/NotificationEditor.vue';
import { notificationEditorPort } from '../../apps/web/src/pages/notification-editor-port';
import { newNotification, secretFields } from '../../apps/web/src/pages/notification-types';
import { useSettingsSecrets } from '../../apps/web/src/pages/settings-secret-state';
import { hostSecretSession } from '../../apps/web/src/pages/host-secret-session';
import { useDashboardLayout } from '../../apps/web/src/pages/dashboard-layout';
import { draftFixture, settle } from './configuration-draft-fixture';

type Check = (value: unknown, name: string) => void;
export async function privateDraftContract(parent: HTMLElement, check: Check): Promise<void> {
  await notification(parent, check);
  await settings(parent, check);
  await layout(check);
}
async function notification(parent: HTMLElement, check: Check) {
  const { store, writes } = draftFixture();
  const task = { ...newNotification('smtp'), id: 'notice', name: 'Saved notification', revision: 4 };
  const holder = document.createElement('section'); parent.append(holder);
  const visible = ref(true), base = notificationEditorPort(store, 'exit-fixture', task.id);
  let reads = 0;
  const port = { ...base, get drafts() { return base.drafts; }, async readCredentials() {
    reads++; return secretFields.map(field => ({ field, revision: reads === 1 ? 7 : 99, isConfigured: false, isReadable: false }));
  } };
  const app = createApp({ render: () => visible.value ? h(NotificationEditor,
    { port, task, channel: 'smtp', catalogRevision: 5, onClose: () => { visible.value = false; } }) : null });
  app.mount(holder); await settle();
  const password = () => holder.querySelector<HTMLInputElement>('[data-secret-field="smtp-password"] input')!;
  const edit = (input: HTMLInputElement, value: string) => { input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })); };
  const secret = crypto.randomUUID();
  try {
    edit(holder.querySelector<HTMLInputElement>('input[aria-label="任务名称"]')!, 'Retained notification');
    edit(password(), secret); await settle();
    holder.querySelector('nexus-modal')!.shadowRoot!.querySelector<HTMLButtonElement>('button[aria-label="关闭"],button[aria-label="Close"]')!.click();
    await settle(); visible.value = true; await settle();
    const draft = base.readDraft()!;
    check(password().value === secret && password().type === 'password' &&
      holder.querySelector<HTMLInputElement>('input[aria-label="任务名称"]')!.value === 'Retained notification',
      'notification close and remount restore ordinary fields and masked sensitive input');
    check(reads === 1 && draft.catalogRevision === 5 && draft.taskRevision === 4 &&
      !JSON.stringify([...store.drafts]).includes(secret),
      'notification remount preserves original revisions and excludes secrets from ordinary drafts');
    [...holder.querySelectorAll('button')].find(button => button.textContent?.trim() === '取消')!.click();
    await settle(); visible.value = true; await settle();
    check(password().value === '' && holder.querySelector<HTMLInputElement>('input[aria-label="任务名称"]')!.value === task.name && writes.length === 0,
      'notification cancellation clears retained input without a business write');
  } finally { app.unmount(); holder.remove(); store.dispose(); }
}
async function settings(parent: HTMLElement, check: Check) {
  const { store, writes } = draftFixture();
  const visible = ref(true), notice = ref('');
  let editor!: ReturnType<typeof useSettingsSecrets>;
  const holder = document.createElement('section'); parent.append(holder);
  const app = createApp({ setup() {
    const owner = { store, get visible() { return visible.value; } };
    editor = useSettingsSecrets(owner, notice);
    return () => h('input', { type: editor.secretVisible.value ? 'text' : 'password', value: editor.secret.value });
  } });
  app.mount(holder); await settle();
  const session = hostSecretSession(store, 'exit-fixture', 'proxy-password');
  session.status = { revision: 3, isConfigured: false, isReadable: true };
  const secret = crypto.randomUUID();
  try {
    editor.editSecret(secret); await settle(); visible.value = false; await settle();
    session.status = { revision: 9, isConfigured: false, isReadable: true };
    visible.value = true; await settle();
    check(holder.querySelector('input')!.value === '' && holder.querySelector('input')!.type === 'password' && editor.secretRevision.value === 9,
      'settings hide and reopen discard unfinished input and use the current secret revision');
    check(!JSON.stringify([...store.drafts]).includes(secret), 'settings secrets remain outside ordinary draft serialization');
    editor.cancelSecret(); await settle();
    check(editor.secret.value === '' && editor.secretMode.value === 'keep' && editor.secretRevision.value === 9 && writes.length === 0,
      'settings explicit cancellation clears only the selected sensitive draft');
    editor.editToken(crypto.randomUUID()); await settle();
    store.editors.dispose();
    check(editor.token.value === '', 'ending the renderer session clears remaining protected inputs');
  } finally { app.unmount(); holder.remove(); store.dispose(); }
}
async function layout(check: Check) {
  const { store, writes } = draftFixture();
  store.state.interface = { revision: 2, operationId: '', values: { theme: 'dark', locale: 'zh-CN',
    dashboardCards: ['status', 'running'], pinnedSettings: [], collapsedSettings: [] } };
  const model = useDashboardLayout(store);
  try {
    store.state.capabilities!.capabilities.push('settings.write', 'settings.read');
    store.saveInterface = async (values, expected, operationId = crypto.randomUUID()) => {
      check(expected === store.state.interface!.revision, 'layout writes use the current committed revision');
      const snapshot = { revision: expected + 1, operationId, values };
      store.state.interface = snapshot; writes.push(snapshot); return snapshot;
    };
    store.inspectInterface = async () => store.state.interface!;
    await model.beginLayout(); model.changeLayout(['running']); await settle();
    check(JSON.stringify(store.state.interface!.values.dashboardCards) === '["running"]',
      'layout changes apply before Save or Close');
    model.closeLayout(); await model.beginLayout();
    check(JSON.stringify(model.layoutCards.value) === '["running"]',
      'reopening layout uses the committed selection');
    model.changeLayout(['status']); await model.cancelLayout();
    check(JSON.stringify(store.state.interface!.values.dashboardCards) === '["running"]',
      'layout Cancel persists the selection from before this opening');
    await model.beginLayout(); const count = writes.length; await model.saveLayout();
    check(writes.length === count && !model.layoutOpen.value, 'unchanged layout Save closes without another settings write');
  } finally { store.dispose(); }
}
