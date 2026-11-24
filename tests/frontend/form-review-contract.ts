import { createApp, h, nextTick, ref } from 'vue';
import type { UiElement } from '@nexuspipeline/extension-sdk';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient } from '../../apps/web/src/control/client';
import { initialValue, schemaAt } from '../../apps/web/src/control/schema';
import type { AutomationBundle } from '../../apps/web/src/control/wire.generated';
import { scriptPaths } from '../../apps/web/src/pages/script-paths';
import ScriptFields from '../../apps/web/src/pages/ScriptFields.vue';
import NotificationSecretInput from '../../apps/web/src/pages/NotificationSecretInput.vue';
import FormSelect from '../../apps/web/src/components/FormSelect';
import { validateForm } from '../../apps/web/src/components/form-validation';

export async function formReviewContract(): Promise<string[]> {
  const checks: string[] = [];
  const check = (value: unknown, name: string) => { if (!value) throw Error(name); checks.push(name); };
  const pending: { path: string; resolve: (response: Response) => void; signal: AbortSignal | undefined }[] = [];
  const store = createStore(new ControlClient(location.origin, async (url, init) => {
    if (!String(url).endsWith('/control/v1/automation/scripts/root')) throw Error('Unexpected root request: ' + url);
    const body = JSON.parse(String(init!.body));
    return await new Promise<Response>(resolve => pending.push({ path: body.rootPath, resolve, signal: init?.signal ?? undefined }));
  }));
  store.state.capabilities = { instanceId: 'root-fixture', hostVersion: '0.18.0', capabilities: ['automation.read'] };
  const script = ref(initialValue(schemaAt('automation-script.schema.json#/$defs/script')) as AutomationBundle['scripts'][number]);
  script.value.id = crypto.randomUUID(); script.value.kind = 'generic';
  const paths = scriptPaths(store, script.value);
  paths.root = ''; paths.executable = ''; paths.arguments = ''; paths.configuration = ''; paths.logs = '';
  const mount = document.createElement('form'); mount.noValidate = true; document.body.append(mount);
  const app = createApp({ render: () => h(ScriptFields, { store, modelValue: script.value,
    'onUpdate:modelValue': value => { script.value = value; } }) });
  app.component('FormSelect', FormSelect);
  const labels = ['脚本主程序路径', '脚本启动参数', '配置文件路径/文件夹', '日志路径'];
  const fields = () => labels.map(label => mount.querySelector<HTMLElement>(`[aria-label="${label}"]`)!);
  const locked = () => fields().every(element => !!element.shadowRoot!.querySelector<HTMLInputElement>('input')!.disabled &&
    [...element.shadowRoot!.querySelectorAll('button')].every(button => button.disabled));
  const settle = async () => { await nextTick(); await new Promise(resolve => setTimeout(resolve, 20)); };
  const reply = (index: number, exists: boolean) => pending[index]!.resolve(new Response(JSON.stringify({
    rootPath: pending[index]!.path, isDirectory: exists }), { headers: { 'Content-Type': 'application/json' } }));
  let unmounted = false;
  try {
    app.mount(mount); await settle();
    check(locked(), 'all four fields and every browse button are disabled before a root is supplied');
    for (const root of [' ', 'relative/root', 'D:\\bad*root']) { paths.root = root; await settle(); check(locked(), root + ' keeps dependent fields locked'); }
    check(!pending.length, 'blank and syntactically invalid roots do not issue filesystem queries');
    paths.root = 'D:\\fixture-a'; await until(() => pending.length === 1);
    check(locked() && !validateForm(mount), 'pending directory inspection keeps dependent fields locked and prevents saving');
    reply(0, false); await settle();
    check(locked(), 'a missing Host directory leaves all dependent fields disabled');
    paths.root = 'D:\\fixture-b'; await until(() => pending.length === 2);
    paths.root = 'D:\\fixture-c'; await until(() => pending.length === 3);
    reply(1, true); await settle();
    check(locked() && pending[1]!.signal?.aborted, 'a cancelled old directory result cannot unlock the current root');
    reply(2, true); await settle();
    check(fields().every(element => !element.shadowRoot!.querySelector<HTMLInputElement>('input')!.disabled),
      'only the current existing Host directory unlocks the four dependent fields');
    const log = fields()[3]!;
    check(log.shadowRoot!.querySelector('input')!.ariaDescribedByElements?.some(element => element.textContent?.includes('{yyyy-MM-dd}')),
      'the actual log path field receives date, wildcard and multi-path help');
    paths.root = ''; await settle(); check(locked(), 'clearing a verified root immediately disables all dependent fields again');
    paths.root = 'D:\\fixture-d'; await until(() => pending.length === 4);
    app.unmount(); unmounted = true; reply(3, true); await settle();
    check(pending[3]!.signal?.aborted && !mount.children.length, 'unmount aborts inspection and discards late directory results');
  } finally {
    if (!unmounted) app.unmount(); store.dispose(); mount.remove();
    document.querySelector('nexus-toast')?.dispatchEvent(new CustomEvent('nexus-close'));
  }
  validationPresentation(check);
  await notificationSecretBinding(check);
  return checks;
}

async function notificationSecretBinding(check: (value: unknown, name: string) => void): Promise<void> {
  const value = ref('');
  const field = 'smtp-password';
  const draft = 'fixture-only-' + crypto.randomUUID();
  const mount = document.createElement('div');
  document.body.append(mount);
  const app = createApp({ render: () => h(NotificationSecretInput, {
    field, label: 'Authorization code', value: value.value, configured: true, disabled: false,
    onEdit: (target, next) => { if (target === field) value.value = next; }
  }) });
  app.mount(mount);
  try {
    const input = mount.querySelector('input')!;
    check(input.type === 'password' && !!input.placeholder && input.value === '',
      'a configured notification credential starts with a placeholder and no saved secret');
    input.value = draft;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await nextTick();
    check(value.value === draft && input.value === draft && !input.hasAttribute('value') && !mount.innerHTML.includes(draft),
      'notification credentials update only the live field and its draft');
    value.value = '';
    await nextTick();
    check(!input.value && !input.hasAttribute('value'), 'clearing the notification draft empties the live field');
  } finally { app.unmount(); mount.remove(); }
}

function validationPresentation(check: (value: unknown, name: string) => void): void {
  const form = document.createElement('form'); document.body.append(form);
  const native = document.createElement('input'); native.required = true; form.append(native);
  const email = document.createElement('nexus-text-input') as UiElement<'nexus-text-input'>;
  email.type = 'email'; email.value = 'invalid-email'; form.append(email);
  const area = document.createElement('nexus-text-area') as UiElement<'nexus-text-area'>;
  area.setAttribute('aria-required', 'true'); form.append(area);
  const path = document.createElement('nexus-path-picker') as UiElement<'nexus-path-picker'>;
  path.kinds = ['file', 'folder']; path.browseAllowed = true; path.setAttribute('aria-required', 'true'); form.append(path);
  const number = document.createElement('nexus-number-input') as UiElement<'nexus-number-input'>;
  number.value = 2; form.append(number);
  const card = document.createElement('nexus-collapsible-card') as UiElement<'nexus-collapsible-card'>;
  form.append(card); card.setAttribute('title', 'Settings title');
  try {
    for (const control of [native, path.shadowRoot!.querySelector('input')!]) {
      const event = new Event('invalid', { cancelable: true }); control.dispatchEvent(event);
      check(event.defaultPrevented, 'native validation is suppressed in ' + (control === native ? 'the document' : 'a control shadow root'));
      check(!control.reportValidity(), 'suppressing the bubble preserves invalidity in ' + (control === native ? 'the document' : 'the shadow root'));
    }
    check(document.documentElement.getAttribute('title') === '',
      'the flat tree has an explicit empty title boundary for native hover fallback');
    native.focus();
    check(native.getAttribute('title') === '' && native.validity.valueMissing && !!native.validationMessage,
      'native required fields keep their constraint error while explicit empty title suppresses fallback');
    for (const control of [path, email, area]) {
      const input = control.shadowRoot!.querySelector<HTMLInputElement | HTMLTextAreaElement>('input,textarea')!;
      check(input.getAttribute('title') === '' && !input.validity.valid && !!input.validationMessage,
        control.localName + ' blocks validation-message hover fallback without weakening native constraints');
    }
    number.max = 1;
    check(number.shadowRoot!.querySelector('input')!.validity.rangeOverflow && number.shadowRoot!.querySelector('input')!.getAttribute('title') === '',
      'numeric range validation retains its error behind an empty native tooltip title');
    check(card.title === 'Settings title' && !card.hasAttribute('title'), 'component titles remain functional without native HTML title tooltips');
    card.setAttribute('title', 'Translated title');
    check(card.title === 'Translated title' && !card.hasAttribute('title'), 'translated component titles do not restore native tooltips');
    for (const control of [path, number]) {
      control.setAttribute('aria-invalid', 'true');
      const input = control.shadowRoot!.querySelector('input')!; input.focus();
      const outline = getComputedStyle(control);
      check(outline.outlineStyle === 'solid' && parseFloat(outline.outlineWidth) >= 1,
        control.localName + ' paints validation around the complete control');
      const rect = control.getBoundingClientRect();
      check([...control.shadowRoot!.querySelectorAll('button')].every(button => button.getBoundingClientRect().right <= rect.right + 1),
        control.localName + ' includes every derived button inside the highlighted bounds');
      control.setAttribute('aria-invalid', 'false');
      check(parseFloat(getComputedStyle(control).outlineWidth) === 2, control.localName + ' focus outlines the complete control');
    }
  } finally { form.remove(); }
}
async function until(condition: () => boolean): Promise<void> {
  const deadline = performance.now() + 4000;
  while (!condition()) {
    if (performance.now() > deadline) throw Error('Root inspection fixture timed out');
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}
