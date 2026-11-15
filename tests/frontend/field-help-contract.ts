import { createApp, h, nextTick, ref, withDirectives } from 'vue';
import { fieldHelp } from '../../apps/web/src/components/field-help';
import FormNumber from '../../apps/web/src/components/FormNumber.vue';
import FormSelect from '../../apps/web/src/components/FormSelect';
import ScriptFields from '../../apps/web/src/pages/ScriptFields.vue';
import NotificationSecretInput from '../../apps/web/src/pages/NotificationSecretInput.vue';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient } from '../../apps/web/src/control/client';
import { initialValue, schemaAt } from '../../apps/web/src/control/schema';
import { locale, t } from '../../apps/web/src/app/locale';
import type { AutomationBundle } from '../../apps/web/src/control/wire.generated';

type Check = (value: unknown, name: string) => void;
const helpText = '-1 表示不限制；正整数达到当天成功次数上限后跳过，0 无效。';

export async function fieldHelpContract(fixture: HTMLElement, check: Check) {
  const store = createStore(new ControlClient(location.origin, async () => { throw Error('Unexpected help request'); }));
  const script = ref(initialValue(schemaAt('automation-script.schema.json#/$defs/script')) as AutomationBundle['scripts'][number]);
  script.value.id = crypto.randomUUID();
  script.value.limits = { maximumAttempts: 3, stallTimeoutSeconds: 300, totalTimeoutSeconds: 7200 };
  store.state.capabilities = { instanceId: 'field-help', hostVersion: '0.18.0', capabilities: [] };
  const value = ref('saved value'), changed = ref(0);
  const baseline = JSON.stringify(script.value), originalLocale = locale.value;
  const app = createApp({ render: () => h('form', {
    id: 'field-help-form', onSubmit: (event: Event) => { event.preventDefault(); changed.value++; }
  }, [
    withDirectives(h('nexus-text-input', { id: 'field-help-input', value: value.value,
      'aria-label': 'Help account', placeholder: t('脚本根目录'),
      'onNexus-change': () => { changed.value++; } }), [[fieldHelp, t(helpText)]]),
    withDirectives(h('label', ['Limit', h(FormNumber, { modelValue: null, placeholder: t('-1 表示不限次数') })]), [[fieldHelp, t(helpText)]]),
    withDirectives(h('nexus-switch-setting', { id: 'field-help-switch', title: 'Help switch', value: false }), [[fieldHelp, t(helpText)]]),
    h(NotificationSecretInput, { field: 'webhook-secret', label: 'Webhook 签名密钥', value: '', configured: true, disabled: false }),
    h(ScriptFields, { modelValue: script.value, store, 'onUpdate:modelValue': () => { changed.value++; } })
  ]) });
  app.component('FormSelect', FormSelect);
  app.mount(fixture);
  await nextTick();
  const input = fixture.querySelector<HTMLElement>('#field-help-input')!;
  const inner = input.shadowRoot!.querySelector<HTMLInputElement>('input')!;
  const number = fixture.querySelector('nexus-number-input')!;
  check(number.shadowRoot!.querySelector<HTMLInputElement>('input')!.placeholder === '-1 表示不限次数',
    'the real FormNumber forwards an empty numeric placeholder without changing its value');
  for (const [label, expected] of [['脚本根目录', '脚本根目录'], ['脚本主程序路径', '脚本主程序文件'],
    ['日志路径', '日志文件路径'], ['成功关键字', '逗号表示 AND，多行表示 OR'], ['失败关键字', '命中即判定失败']]) {
    const control = fixture.querySelector(`[aria-label="${label}"]`)!;
    check(control.shadowRoot!.querySelector<HTMLInputElement>('input,textarea')!.placeholder === expected,
      'the real script editor restores the placeholder for ' + label);
  }
  check(fixture.querySelector<HTMLInputElement>('[data-secret-field] input')!.placeholder === '留空不修改',
    'configured secrets retain their existing keep placeholder');
  const described = () => inner.ariaDescribedByElements?.map(item => item.textContent).join(' ');
  check(described() === helpText, 'the actual shadow input receives its help description before keyboard focus');
  const switchInput = fixture.querySelector('#field-help-switch')!.shadowRoot!
    .querySelector('nexus-switch')!.shadowRoot!.querySelector('button')!;
  check(switchInput.ariaDescribedByElements?.some(item => item.textContent === helpText),
    'nested switch controls receive descriptions across the owner shadow roots');
  locale.value = 'en-US'; await nextTick();
  check(inner.placeholder === 'Script root directory' && described()?.startsWith('-1 means unlimited.'),
    'changing language updates existing placeholders and help descriptions');
  check(fixture.querySelector('#field-help-input') === input && inner.value === 'saved value',
    'language updates preserve the input node and saved value');
  locale.value = originalLocale; await nextTick();
  check(changed.value === 0 && JSON.stringify(script.value) === baseline && store.dirty.size === 0,
    'mounting and translating help neither submit the form nor mark a draft changed');
  return {
    unchanged: () => changed.value === 0 && JSON.stringify(script.value) === baseline && store.dirty.size === 0,
    dispose: () => { app.unmount(); store.dispose(); locale.value = originalLocale; }
  };
}
