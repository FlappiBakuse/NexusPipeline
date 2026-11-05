import { createApp, h, nextTick } from 'vue';
import FormSelect from '../../apps/web/src/components/FormSelect';
import FormNumber from '../../apps/web/src/components/FormNumber.vue';
import FormDate from '../../apps/web/src/components/FormDate.vue';
import { locale, t } from '../../apps/web/src/app/locale';
import type { UiElement } from '@nexuspipeline/extension-sdk';

export async function formLabelContract(fixture: HTMLElement,
  check: (condition: unknown, name: string) => void): Promise<void> {
  const original = locale.value;
  const holder = document.createElement('section'); fixture.append(holder);
  const app = createApp({ setup: () => () => h('div', [
    h('label', [locale.value === 'en-US' ? 'Count' : '数量', h(FormNumber, { modelValue: 20, min: 10 })]),
    h('label', [locale.value === 'en-US' ? 'Mode' : '模式', h(FormSelect, { modelValue: 'one' },
      { default: () => [h('option', { value: 'one' }, t('信息')),
        h('option', { value: 'fatal' }, t('致命')), h('option', { value: 'proxy' }, t('不使用代理'))] })]),
    h('label', [locale.value === 'en-US' ? 'Date' : '日期', h(FormDate, { modelValue: '2030-01-01' })])
  ]) });
  app.mount(holder); await nextTick();
  const number = holder.querySelector('nexus-number-input')!;
  const input = number.shadowRoot!.querySelector('input')!;
  const select = holder.querySelector('nexus-select')! as UiElement<'nexus-select'>;
  const date = holder.querySelector('.form-date')!.shadowRoot!.querySelector('input')!;
  input.focus(); input.value = '9'; input.dispatchEvent(new Event('change', { bubbles: true }));
  try {
    for (const language of ['en-US', 'zh-CN'] as const) {
      locale.value = language; await nextTick(); await nextTick();
      check(number.getAttribute('aria-label') === (language === 'en-US' ? 'Count' : '数量'),
        language + ': mounted number inherits the translated label');
      check(select.getAttribute('aria-label') === (language === 'en-US' ? 'Mode' : '模式'),
        language + ': mounted select inherits the translated label');
      check(select.options.map(option => option.label).join('|') ===
        (language === 'en-US' ? 'Info|Fatal|No proxy' : '信息|致命|不使用代理'),
        language + ': mounted select options translate before value-label routes load');
      check(select.shadowRoot!.querySelector('input')!.value === (language === 'en-US' ? 'Info' : '信息'),
        language + ': the selected option follows the current locale without changing its value');
      check(date.getAttribute('aria-label') === (language === 'en-US' ? 'Date' : '日期'),
        language + ': mounted date inherits the translated label');
      check(number.shadowRoot!.querySelector('input') === input && input.value === '9'
        && input.getRootNode() instanceof ShadowRoot && number.shadowRoot!.activeElement === input,
        language + ': locale refresh retains focused invalid number draft and node identity');
    }
  } finally { app.unmount(); holder.remove(); locale.value = original; }
}
