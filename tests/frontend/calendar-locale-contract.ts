import type { UiElement } from '@nexuspipeline/extension-sdk';
import { DateControl } from '../../apps/web/src/ui/date-control';

export function calendarLocaleContract(fixture: HTMLElement, check: (condition: unknown, name: string) => void): void {
  singleDateContract(fixture, check);
  const document = fixture.ownerDocument;
  const language = document.documentElement.lang;
  try {
    for (const locale of ['', 'zh-CN', 'en-US']) {
      document.documentElement.lang = locale;
      const picker = document.createElement('nexus-date-range-picker') as UiElement<'nexus-date-range-picker'>;
      picker.value = { from: '2024-02-28', to: '2024-02-29' };
      fixture.append(picker);
      for (let opening = 0; opening < 2; opening++) {
        const root = picker.shadowRoot!;
        root.querySelector<HTMLButtonElement>('.trigger')!.click();
        check(picker.value.from === '2024-02-28' && picker.value.to === '2024-02-29',
          `opening and closing preserves the selected leap-day range for locale ${locale || 'default'}`);
        const dialog = root.querySelector<HTMLElement>('[role=dialog]')!;
        check(dialog.getClientRects().length > 0, `the calendar opens for locale ${locale || 'default'}`);
        dialog.dispatchEvent(
          new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        check(!root.querySelector<HTMLElement>('[role=dialog]')!.getClientRects().length &&
          picker.value.from === '2024-02-28' && picker.value.to === '2024-02-29',
          `Escape closes the calendar without changing the leap-day range for locale ${locale || 'default'}`);
      }
      picker.remove();
    }
  } finally { document.documentElement.lang = language; }
}

function singleDateContract(fixture: HTMLElement, check: (condition: unknown, name: string) => void): void {
  const holder = document.createElement('span'); fixture.append(holder);
  const root = holder.attachShadow({ mode: 'open' });
  const commits: string[] = [];
  const control = new DateControl(root, value => commits.push(value));
  const day = (value: string) => root.querySelector<HTMLButtonElement>(`[data-action=date][data-value="${value}"]`)!;
  const open = () => control.field.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
  try {
    control.set({ value: '2024-02-28', label: 'Date', min: '2024-02-27', max: '2024-03-02' });
    open();
    check(root.querySelectorAll('[aria-selected=true]').length === 1 && day('2024-02-28').getAttribute('aria-selected') === 'true',
      'a single-date calendar marks only its committed date as selected');
    check(root.activeElement === day('2024-02-28'), 'opening by keyboard focuses the enabled selected date');
    day('2024-02-28').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    check(root.activeElement === day('2024-02-29') && !commits.length && day('2024-02-28').getAttribute('aria-selected') === 'true',
      'keyboard exploration reaches leap day without committing or moving the selected state');
    day('2024-02-29').click();
    check(commits.length === 1 && commits[0] === '2024-02-29' && root.activeElement === control.field,
      'choosing the focused day commits once and restores input focus');
    open();
    check(root.querySelectorAll('[aria-selected=true]').length === 1 && root.activeElement === day('2024-02-29'),
      'reopening focuses the newly committed date');
    day('2024-02-29').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    root.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    check(control.field.value === '2024-02-29' && commits.length === 1 && !root.querySelector('.popup')!.matches(':popover-open'),
      'Escape discards keyboard exploration without changing the committed date');
    control.set({ value: '', label: 'Date' }); open();
    check(root.querySelectorAll('[aria-selected=true]').length === 0, 'an empty date has no selected calendar cell');
  } finally { control.dispose(); holder.remove(); }
}
