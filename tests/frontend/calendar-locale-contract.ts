import type { UiElement } from '@nexuspipeline/extension-sdk';

export function calendarLocaleContract(fixture: HTMLElement, check: (condition: unknown, name: string) => void): void {
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
        root.querySelector<HTMLElement>('[role=dialog]')!.dispatchEvent(
          new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      }
      picker.remove();
    }
  } finally { document.documentElement.lang = language; }
}
