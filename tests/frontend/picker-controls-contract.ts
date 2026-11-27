import type { UiElement, UiElementName } from '@nexuspipeline/extension-sdk';
import theme from '../../apps/web/src/styles/theme.css?inline';
import uiTheme from '../../apps/web/src/ui/theme.css?inline';

const create = <N extends UiElementName>(name: N): UiElement<N> =>
  document.createElement(name) as UiElement<N>;
type Check = (condition: unknown, name: string) => void;

export function compositeRadiusContract(fixture: HTMLElement, check: Check): void {
  const style = document.createElement('style');
  style.textContent = theme + uiTheme;
  const group = document.createElement('section');
  const path = create('nexus-path-picker');
  const number = create('nexus-number-input');
  const color = create('nexus-color-picker');
  const button = create('nexus-button');
  const card = create('nexus-card');
  const modal = create('nexus-modal');
  const switches = create('nexus-switch-list');
  const badge = create('nexus-badge');
  path.browseAllowed = true;
  group.append(path, number, color, button, card, modal, switches, badge);
  fixture.append(style, group);
  const radius = (element: Element) => {
    const corners = getComputedStyle(element);
    return [corners.borderTopLeftRadius, corners.borderTopRightRadius,
      corners.borderBottomRightRadius, corners.borderBottomLeftRadius].join(' ');
  };
  try {
    const pathRoot = path.shadowRoot!;
    const pathInput = pathRoot.querySelector('input')!;
    const corners = '8px 8px 8px 8px';
    path.kinds = [];
    check(radius(path) === corners && radius(pathInput) === corners,
      'path field without buttons has four rounded outer corners');
    path.kinds = ['file'];
    check(radius(pathInput) === '8px 0px 0px 8px' && radius(pathRoot.querySelector('button')!) === '0px 8px 8px 0px',
      'path field with one button has square shared corners');
    path.kinds = ['file', 'folder'];
    const [file, folder] = pathRoot.querySelectorAll('button');
    check(radius(file) === '0px 0px 0px 0px' && radius(folder) === '0px 8px 8px 0px' &&
      getComputedStyle(file).borderLeftWidth === '0px' && getComputedStyle(folder).borderLeftWidth === '0px',
      'path field with two buttons has a single divider and only the end button rounds');
    pathInput.focus();
    check(getComputedStyle(path).outlineWidth === '2px' && radius(path) === corners,
      'path field focus follows the full rounded outline');
    pathInput.blur();
    path.setAttribute('aria-invalid', 'true');
    check(getComputedStyle(path).outlineWidth === '1px' && radius(path) === corners,
      'path field validation follows the full rounded outline');
    path.disabled = true;
    check(getComputedStyle(path).outlineStyle === 'none' && radius(path) === corners,
      'disabled path field keeps its rounded outline geometry');

    const numberRoot = number.shadowRoot!;
    const [minus, plus] = numberRoot.querySelectorAll('button');
    check(radius(number) === corners && radius(numberRoot.querySelector('input')!) === corners &&
      radius(plus) === '0px 7px 0px 0px' && radius(minus) === '0px 0px 7px 0px',
      'numeric step buttons follow only the outer corners inside the field border');
    const colorRoot = color.shadowRoot!;
    check(radius(colorRoot.querySelector('.row')!) === corners &&
      radius(colorRoot.querySelector('.value-input')!) === '8px 0px 0px 8px' &&
      radius(colorRoot.querySelector('.trigger')!) === '0px 8px 8px 0px',
      'color field keeps its common outer shape and square shared corners');
    const surfaceCorners = '10px 10px 10px 10px';
    check(radius(card) === surfaceCorners && radius(modal.shadowRoot!.querySelector('.panel')!) === surfaceCorners &&
      radius(switches) === surfaceCorners && radius(colorRoot.querySelector('.panel')!) === surfaceCorners,
      'cards, modals, nested panels and popovers share a 10px radius');
    check(radius(button.shadowRoot!.querySelector('button')!) === corners && radius(badge) === '6px 6px 6px 6px',
      'buttons and badges use their own control radius');
  } finally {
    group.remove();
    style.remove();
  }
}
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
