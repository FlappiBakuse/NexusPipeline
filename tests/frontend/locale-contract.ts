import { formLabelContract } from './form-label-contract';
import { DateControl } from '../../apps/web/src/ui/date-control.js';
import { locale, t } from '../../apps/web/src/app/locale.js';
import { errorMessages } from '../../apps/web/src/app/error-messages.js';
import type { UiElement } from '@nexuspipeline/extension-sdk';

export async function localeContract(fixture: HTMLElement, check: (condition: unknown, name: string) => void): Promise<void> {
  await formLabelContract(fixture, check);
  const original = document.documentElement.lang, originalLocale = locale.value;
  const number = document.createElement('nexus-number-input') as UiElement<'nexus-number-input'>;
  number.value = 20; number.min = 10; number.max = 120;
  const color = document.createElement('nexus-color-picker') as UiElement<'nexus-color-picker'>;
  color.value = '#334455';
  const holder = document.createElement('span');
  const toast = document.createElement('nexus-toast') as UiElement<'nexus-toast'>;
  fixture.append(number, color, holder, toast);
  const root = holder.attachShadow({ mode: 'open' });
  const date = new DateControl(root, () => { throw new Error('Locale changes must not commit date drafts'); });
  date.set({ value: '2030-01-01', kind: 'date', label: 'Date' });
  const numeric = number.shadowRoot!.querySelector('input')!;
  numeric.value = '9';
  numeric.dispatchEvent(new Event('change', { bubbles: true }));
  const hex = color.shadowRoot!.querySelector<HTMLInputElement>('.value-input')!;
  hex.value = '#nothex';
  hex.dispatchEvent(new Event('change', { bubbles: true }));
  date.field.value = '2030-02-31';
  date.field.dispatchEvent(new Event('change', { bubbles: true }));
  try {
    for (const language of ['en-US', 'zh-CN'] as const) {
      locale.value = language;
      document.documentElement.lang = language;
      await new Promise(resolve => setTimeout(resolve, 0));
      date.set({ value: '2030-01-01', kind: 'date', label: language === 'en-US' ? 'Date' : '日期' });
      check(numeric.value === '9' && number.value === 20 && number.getAttribute('aria-invalid') === 'true',
        language + ': invalid number text survives a locale refresh');
      check(date.field.value === '2030-02-31' && date.field.getAttribute('aria-invalid') === 'true',
        language + ': blurred invalid date text survives a translated label');
      check(hex.value === '#nothex' && color.value === '#334455',
        language + ': invalid color text survives a locale refresh');
      check(number.shadowRoot!.querySelector('button')!.getAttribute('aria-label') ===
        (language === 'en-US' ? 'Decrease' : '减少'), language + ': existing control chrome translates');
      check(t('游戏路径') === (language === 'en-US' ? 'Game path' : '游戏路径'),
        language + ': field messages follow the current locale');
      check(t('视图设置保存失败，选择已保留。') === (language === 'en-US'
        ? 'View settings save failed; choices preserved.' : '视图设置保存失败，选择已保留。'),
        language + ': stored dynamic outcomes translate when rendered');
      toast.message = t('project_script_missing');
      check(toast.shadowRoot!.querySelector('span')!.textContent === (language === 'en-US'
        ? 'Save the script instance before reading its project configuration.' : '请先保存脚本实例，再读取项目配置。'),
        language + ': project failure toast explains the required action');
      for (const code of Object.keys(errorMessages)) {
        const text = t(code);
        check((text !== code || code.includes(' ')) && (language === 'en-US' ? !/[\u4e00-\u9fff]/.test(text) : /[\u4e00-\u9fff]/.test(text)),
          language + ': known feedback has a localized explanation: ' + code);
      }
      toast.message = 'unknown_fixture_failure';
      check(toast.shadowRoot!.querySelector('span')!.textContent?.includes(language === 'en-US'
        ? 'Diagnostic code: unknown_fixture_failure' : '错误码：unknown_fixture_failure'),
        language + ': an unknown toast code keeps a localized explanation and its diagnostic identity');
    }
  } finally {
    date.dispose(); number.remove(); color.remove(); holder.remove(); toast.remove();
    document.documentElement.lang = original; locale.value = originalLocale;
  }
}
