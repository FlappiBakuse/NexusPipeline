import type { UiElement, UiElementName } from '@nexuspipeline/extension-sdk';

const create = <N extends UiElementName>(name: N): UiElement<N> =>
  document.createElement(name) as UiElement<N>;
type Check = (condition: unknown, name: string) => void;
export function timeContract(fixture: HTMLElement, check: Check): void {
  const time = create('nexus-time-picker');
  time.value = '00:58';
  fixture.append(time);
  const root = time.shadowRoot!.querySelector('span')!.shadowRoot!;
  root.querySelector<HTMLButtonElement>('.trigger')!.click();
  root.querySelector<HTMLButtonElement>('[aria-label="Decrease hour"]')!.click();
  root.querySelector<HTMLButtonElement>('[data-unit=minute][data-value="59"]')!.click();
  check(time.value === '23:59', 'custom time columns commit a 24-hour HH:mm value');
  time.disabled = true;
  root.querySelector<HTMLButtonElement>('.trigger')!.click();
  check(
    !root.querySelector('[role=dialog]')!.matches(':popover-open'),
    'disabled time picker cannot open a selection surface'
  );
  time.remove();
}

export function colorContract(fixture: HTMLElement, check: Check): void {
  const color = create('nexus-color-picker');
  color.value = '#112233';
  fixture.append(color);
  const root = color.shadowRoot!,
    hex = () => root.querySelector<HTMLInputElement>('.hex')!;
  let changes = 0;
  color.addEventListener('nexus-change', () => changes++);
  root.querySelector<HTMLButtonElement>('.trigger')!.click();
  hex().value = '#ff8800';
  hex().dispatchEvent(new Event('change', { bubbles: true }));
  check(color.value === '#ff8800' && changes === 1, 'custom color editor commits a valid HEX value once');
  const channel = root.querySelector<HTMLInputElement>('[data-channel="0"]')!;
  channel.value = '17';
  channel.dispatchEvent(new Event('input', { bubbles: true }));
  check(color.value === '#118800' && changes === 2, 'RGB channel edits retain the other two color channels');
  hex().value = 'invalid';
  hex().dispatchEvent(new Event('change', { bubbles: true }));
  check(color.value === '#118800' && changes === 2, 'invalid color text does not replace a committed color');
  color.remove();
  check(
    !root.querySelector('[role=dialog]')!.matches(':popover-open'),
    'disconnect releases the color editor surface'
  );
}
