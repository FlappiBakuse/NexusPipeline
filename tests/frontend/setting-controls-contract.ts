import type { UiElement, UiElementName } from '@nexuspipeline/extension-sdk';

const create = <N extends UiElementName>(name: N): UiElement<N> =>
  document.createElement(name) as UiElement<N>;
type Check = (condition: unknown, name: string) => void;

export function settingContract(fixture: HTMLElement, check: Check): void {
  for (const name of ['nexus-collapsible-card', 'nexus-schedule-card'] as const) {
    const card = create(name);
    card.expanded = true;
    const content = document.createElement('input');
    if (name === 'nexus-schedule-card') content.slot = 'days';
    card.append(content); fixture.append(card);
    const heading = card.shadowRoot!.querySelector('button')!.getBoundingClientRect();
    const body = content.getBoundingClientRect();
    check(heading.height > 0 && body.width > 0 && body.height > 0 && body.top >= heading.bottom,
      name + ' places visible content below its heading');
    card.remove();
  }
  const setting = create('nexus-switch-setting');
  setting.title = 'Enabled';
  fixture.append(setting);
  const toggle = setting.shadowRoot!.querySelector<UiElement<'nexus-switch'>>('nexus-switch')!;
  let changes = 0;
  setting.addEventListener('nexus-change', () => changes++);
  toggle.shadowRoot!.querySelector<HTMLButtonElement>('button')!.click();
  check(
    setting.value && toggle.value && changes === 1,
    'switch setting uses the shared switch and emits exactly one change'
  );
  setting.disabled = true;
  toggle.shadowRoot!.querySelector<HTMLButtonElement>('button')!.click();
  check(setting.value && changes === 1, 'disabled switch settings cannot change their shared switch');
  setting.remove();
  const list = create('nexus-switch-list');
  list.style.setProperty('--nx-color-border', '#394152');
  const first = create('nexus-switch-setting'), second = create('nexus-switch-setting'), last = create('nexus-switch-setting');
  list.append(first, second, last);
  fixture.append(list);
  const border = (row: HTMLElement) => getComputedStyle(row).borderTopWidth;
  check(getComputedStyle(list).borderTopWidth === '1px' && border(first) === '0px' &&
    border(second) === '1px' && border(last) === '1px', 'integrated switch lists paint one shared frame and a separator before each subsequent row');
  first.hidden = true;
  check(!first.getClientRects().length && border(second) === '0px' && border(last) === '1px',
    'hiding a switch keeps separators between visible rows only');
  second.remove();
  check(border(last) === '0px', 'the last visible switch does not keep a separator when it becomes the only row');
  list.remove();
}
