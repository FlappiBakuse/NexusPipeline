import { settingContract } from './setting-controls-contract.js';
import { calendarLocaleContract } from './calendar-locale-contract.js';
import { timeContract, colorContract } from './picker-controls-contract.js';
import type { UiElement, UiElementName } from '@nexuspipeline/extension-sdk';

const create = <N extends UiElementName>(name: N): UiElement<N> =>
  document.createElement(name) as UiElement<N>;
type Check = (condition: unknown, name: string) => void;
export function paintedControls(fixture: HTMLElement, check: Check): void {
  numberContract(fixture, check);
  textContract(fixture, check);
  fileContract(fixture, check);
  const icon = create('nexus-icon-button');
  icon.label = 'Pause';
  icon.setAttribute('aria-pressed', 'false');
  fixture.append(icon);
  const button = icon.shadowRoot!.querySelector('button')!;
  check(
    button.getAttribute('aria-pressed') === 'false',
    'icon actions expose their initial pressed state to assistive technology'
  );
  icon.setAttribute('aria-pressed', 'true');
  check(button.getAttribute('aria-pressed') === 'true', 'icon actions update their accessible pressed state');
  icon.remove();
  const toggle = create('nexus-button');
  toggle.label = 'Sunday';
  toggle.setAttribute('aria-pressed', 'false');
  fixture.append(toggle);
  const toggleButton = toggle.shadowRoot!.querySelector('button')!;
  check(
    toggleButton.getAttribute('aria-pressed') === 'false',
    'ordinary action toggles expose their initial accessible pressed state'
  );
  toggle.setAttribute('aria-pressed', 'true');
  check(
    toggleButton.getAttribute('aria-pressed') === 'true',
    'ordinary action toggles update their accessible pressed state'
  );
  toggle.remove();
  const modal = create('nexus-modal'),
    draft = document.createElement('input');
  draft.value = 'ordinary draft';
  modal.append(draft);
  fixture.append(modal);
  modal.open = true;
  const dialog = modal.shadowRoot!.querySelector('dialog')!;
  modal.hidden = true;
  check(
    !dialog.open && draft.value === 'ordinary draft',
    'hiding an owned modal immediately closes its native surface while retaining ordinary draft nodes'
  );
  modal.hidden = false;
  check(dialog.open && draft.isConnected, 'restoring an owned modal reopens the same ordinary draft');
  modal.remove();
  selectContract(fixture, check);
  calendarContract(fixture, check);
  timeContract(fixture, check);
  pagerContract(fixture, check);
  colorContract(fixture, check);
  settingContract(fixture, check);
}

function textContract(fixture: HTMLElement, check: Check): void {
  const area = create('nexus-text-area');
  area.setAttribute('aria-required', 'true');
  area.setAttribute('placeholder', 'Output JSON');
  fixture.append(area);
  const textArea = area.shadowRoot!.querySelector('textarea')!;
  check(
    !textArea.checkValidity() && textArea.placeholder === 'Output JSON',
    'required text areas expose native validity and their supplied placeholder'
  );
  area.value = 'return { success: true };';
  check(textArea.checkValidity(), 'a corrected required text area accepts the current code draft');
  area.remove();
  const overflow = create('nexus-overflow-text');
  overflow.text = 'Visible task title';
  fixture.append(overflow);
  check(
    overflow.shadowRoot!.querySelector('.fallback')!.textContent === 'Visible task title' &&
      overflow.getBoundingClientRect().width > 0,
    'public overflow text renders its property as visible content'
  );
  overflow.text = 'Updated task title';
  check(
    overflow.shadowRoot!.querySelector('.fallback')!.textContent === 'Updated task title' &&
      Number.isFinite(overflow.getBoundingClientRect().width) && overflow.getBoundingClientRect().width > 0,
    'overflow measurement retains finite geometry after a public text update'
  );
  overflow.remove();
  const form = document.createElement('form'),
    text = create('nexus-text-input');
  text.setAttribute('aria-required', 'true');
  form.append(text);
  fixture.append(form);
  const input = text.shadowRoot!.querySelector('input')!;
  check(
    input.required && input.getAttribute('aria-required') === 'true' && !form.checkValidity(),
    'required text controls expose their requirement and invalidate an empty containing form'
  );
  text.value = 'Complete';
  check(form.checkValidity(), 'a valid required text value clears the containing form validity');
  form.remove();
}

function fileContract(fixture: HTMLElement, check: Check): void {
  const picker = create('nexus-file-picker');
  picker.multiple = true;
  picker.setAttribute('aria-label', 'Add images');
  fixture.append(picker);
  const root = picker.shadowRoot!,
    input = root.querySelector('input')!,
    transfer = new DataTransfer();
  transfer.items.add(new File(['first'], 'first.png', { type: 'image/png' }));
  transfer.items.add(new File(['second'], 'second.png', { type: 'image/png' }));
  let count = 0;
  picker.addEventListener('nexus-change', (event) => {
    count = (event as CustomEvent<{ value: File[] }>).detail.value.length;
  });
  input.files = transfer.files;
  input.dispatchEvent(new Event('change'));
  check(
    root.querySelector('slot')!.textContent === 'Add images' &&
      count === 2 &&
      root.querySelector('.filename')!.textContent === 'first.png, second.png',
    'file selection displays the supplied caption and selected filenames while emitting both files'
  );
  picker.disabled = true;
  check(
    root.querySelector('button')!.disabled &&
      input.disabled &&
      root.querySelector('.filename')!.textContent!.includes('first.png'),
    'disabling a file picker preserves its selected filename summary and blocks both controls'
  );
  picker.remove();
}

function numberContract(fixture: HTMLElement, check: Check): void {
  const number = create('nexus-number-input');
  number.value = 30;
  number.min = -1;
  number.max = 120;
  number.setAttribute('aria-required', 'true');
  fixture.append(number);
  const input = number.shadowRoot!.querySelector('input')!;
  let changes = 0;
  number.addEventListener('nexus-change', () => changes++);
  input.value = '121';
  input.dispatchEvent(new Event('change'));
  check(
    number.value === 30 && number.getAttribute('aria-invalid') === 'true' && changes === 0,
    'numeric consumers can read invalidity without inspecting the control internals'
  );
  number.readOnly = true;
  number.readOnly = false;
  check(
    input.value === '121' && number.value === 30,
    'unrelated control rendering preserves invalid numeric text'
  );
  for (const raw of ['10.5', '']) {
    input.value = raw;
    input.dispatchEvent(new Event('change'));
    check(
      number.getAttribute('aria-invalid') === 'true' && changes === 0,
      raw
        ? 'fractional input cannot silently commit an integer field'
        : 'a required empty numeric field cannot reuse its old value'
    );
  }
  input.value = '45';
  input.dispatchEvent(new Event('change'));
  check(
    number.value === 45 && number.getAttribute('aria-invalid') === 'false' && changes === 1,
    'corrected numeric text commits once and clears public invalidity'
  );
  number.max = 45;
  const increase = number.shadowRoot!.querySelector<HTMLButtonElement>('[aria-label=Increase]')!;
  increase.click();
  check(
    increase.disabled && changes === 1 && number.value === 45,
    'stepping above the maximum is disabled and emits no repeated value'
  );
  number.min = 45;
  const decrease = number.shadowRoot!.querySelector<HTMLButtonElement>('[aria-label=Decrease]')!;
  decrease.click();
  check(
    decrease.disabled && changes === 1 && number.value === 45,
    'stepping below the minimum is disabled and emits no repeated value'
  );
  number.remove();
}

function selectContract(fixture: HTMLElement, check: Check): void {
  const select = create('nexus-select');
  select.options = [
    { id: 'one', label: 'One' },
    { id: 'blocked', label: 'Blocked', disabled: true },
    { id: 'two', label: 'Two' }
  ];
  select.value = 'one';
  fixture.append(select);
  const field = select.shadowRoot!.querySelector('input')!;
  let changes = 0,
    value: unknown;
  select.addEventListener('nexus-change', (event) => {
    changes++;
    value = (event as CustomEvent<{ value: unknown }>).detail.value;
  });
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
  const list = select.shadowRoot!.querySelector<HTMLElement>('[role=listbox]')!;
  check(
    list.matches(':popover-open') && select.shadowRoot!.activeElement?.textContent === 'One',
    'custom select opens its owned option list from the keyboard'
  );
  window.dispatchEvent(new Event('scroll'));
  check(
    list.matches(':popover-open'),
    'a delayed scroll event at the same anchor cannot dismiss a newly opened option list'
  );
  select.shadowRoot!.activeElement!.dispatchEvent(
    new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })
  );
  check(
    select.shadowRoot!.activeElement?.textContent === 'Two',
    'select arrow navigation skips disabled options'
  );
  (select.shadowRoot!.activeElement as HTMLButtonElement).click();
  check(
    select.value === 'two' && value === 'two' && changes === 1 && !list.matches(':popover-open'),
    'choosing an option commits one ABI value and closes the owned list'
  );
  field.click();
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  check(
    changes === 1 && !list.matches(':popover-open'),
    'Escape dismisses a select without changing its value'
  );
  select.multiple = true;
  field.click();
  select.shadowRoot!.querySelector<HTMLButtonElement>('[data-id=one]')!.click();
  check(
    Array.isArray(select.value) && select.value.includes('one') && select.value.includes('two'),
    'multiple selection retains both selected identities'
  );
  select.style.transform = 'translateY(1px)';
  window.dispatchEvent(new Event('scroll'));
  check(
    !list.matches(':popover-open'),
    'scrolling that moves the anchor closes an option list instead of leaving it detached'
  );
  select.remove();
  check(!list.matches(':popover-open'), 'disconnect releases the select top-layer surface');

  const large = create('nexus-select');
  large.searchable = true;
  large.options = Array.from({ length: 4096 }, (_, index) => ({ id: String(index), label: 'File ' + index }));
  fixture.append(large);
  const search = large.shadowRoot!.querySelector('input')!;
  search.value = 'File 4095';
  search.dispatchEvent(new Event('input'));
  const last = large.shadowRoot!.querySelector<HTMLButtonElement>('[data-id="4095"]');
  check(
    last && search.value === 'File 4095',
    'the first typed search retains its text and reaches the last of 4096 files'
  );
  last!.click();
  check(large.value === '4095', 'a large collection can commit its final identity');
  search.dispatchEvent(new InputEvent('beforeinput', { inputType: 'insertText', data: 'F', bubbles: true }));
  check(
    search.value === '',
    'typing into a closed searchable control replaces the selected label before insertion'
  );
  search.value = 'File 4095';
  search.dispatchEvent(new Event('input'));
  check(
    search.value === 'File 4095' && large.shadowRoot!.querySelector('[data-id="4095"]'),
    'the inserted first search text is retained after replacing a selected label'
  );
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  search.click();
  check(
    large.shadowRoot!.querySelectorAll('button[data-id]').length <= 80,
    'large selectors keep a bounded option window'
  );
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
  check(
    large.shadowRoot!.activeElement?.getAttribute('data-id') === '4095',
    'End navigates to the final option across windows'
  );
  large.remove();
}

function calendarContract(fixture: HTMLElement, check: Check): void {
  calendarLocaleContract(fixture, check);
  const range = create('nexus-date-range-picker');
  range.value = { from: '2024-02-28', to: '2024-02-29' };
  range.min = '2024-02-01';
  range.max = '2024-03-03';
  const endpoint = 'rgb(30, 60, 90)', middle = 'rgb(40, 70, 100)';
  range.style.setProperty('--nx-color-accent', endpoint);
  range.style.setProperty('--accent-soft', middle);
  range.style.setProperty('--nx-motion-duration-fast', '0ms');
  fixture.append(range);
  const root = range.shadowRoot!;
  const trigger = root.querySelector<HTMLButtonElement>('.trigger')!;
  const panel = root.querySelector<HTMLElement>('[role=dialog]')!;
  const day = (date: string) => root.querySelector<HTMLButtonElement>(`[data-date="${date}"]`)!;
  const fill = (date: string) => getComputedStyle(day(date)).backgroundColor;
  const apply = root.querySelector<HTMLButtonElement>('.apply')!;
  let changes = 0;
  range.addEventListener('nexus-change', () => changes++);
  trigger.click();
  check(
    panel.matches(':popover-open') && root.querySelectorAll('.months section').length === 2,
    'one date-range trigger opens two owned calendar months'
  );
  check(
    day('2024-02-29') && !day('2024-02-30'),
    'the calendar exposes leap day without normalizing impossible dates'
  );
  check(
    root.querySelector<HTMLButtonElement>('[data-month="-1"]')!.disabled &&
      root.querySelector<HTMLButtonElement>('[data-month="1"]')!.disabled,
    'month navigation respects the first and last two-month windows'
  );
  day('2024-03-04').click();
  check(day('2024-03-04').disabled && changes === 0, 'out-of-range day buttons cannot commit');
  day('2024-02-27').click();
  day('2024-03-02').dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
  check(
    fill('2024-03-02') === endpoint &&
      fill('2024-02-28') === middle &&
      range.value.from === '2024-02-28' &&
      changes === 0,
    'range hover previews both endpoints without committing the draft'
  );
  panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  check(
    !panel.matches(':popover-open') && root.activeElement === trigger && changes === 0,
    'range Escape discards the preview and restores trigger focus'
  );
  trigger.click();
  check(
    fill('2024-02-28') === endpoint,
    'reopening restores the authoritative range, not the cancelled preview'
  );
  day('2024-02-29').click();
  day('2024-02-27').click();
  apply.click();
  check(
    range.value.from === '2024-02-27' &&
      range.value.to === '2024-02-29' &&
      changes === 1 &&
      root.activeElement === trigger &&
      !panel.matches(':popover-open'),
    'reverse date selection applies one ordered ABI range and closes the popup'
  );
  let rejected = false;
  try {
    range.value = { from: '2024-02-30', to: '2024-03-01' };
  } catch {
    rejected = true;
  }
  check(
    rejected && range.value.from === '2024-02-27' && changes === 1,
    'impossible external dates are rejected without replacing the committed range'
  );
  trigger.click();
  day('2024-02-28').focus();
  day('2024-02-28').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
  check(root.activeElement === day('2024-02-29'), 'calendar arrow navigation reaches local leap day');
  day('2024-02-29').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
  check(
    root.activeElement === day('2024-03-01'),
    'calendar keyboard navigation crosses a month without UTC conversion'
  );
  range.value = { from: '2024-02-27', to: '2024-02-29' };
  check(
    panel.matches(':popover-open') && root.activeElement === day('2024-03-01'),
    'equal consumer props do not close a range or replace its focused calendar day'
  );
  range.value = { from: '2024-03-01', to: '2024-03-03' };
  check(
    !panel.matches(':popover-open') && changes === 1,
    'an external range update discards a stale open draft without an emitted write'
  );
  trigger.click();
  range.max = '2024-03-02';
  check(!panel.matches(':popover-open') && changes === 1, 'changed bounds close an obsolete open range');
  range.inert = true;
  trigger.click();
  check(!panel.matches(':popover-open'), 'an inert range cannot open a calendar');
  range.inert = false;
  range.value = { from: null, to: null };
  range.min = '2028-02-29';
  range.max = '2028-02-29';
  trigger.click();
  check(
    day('2028-02-29') && !day('2028-02-29').disabled && apply.disabled,
    'an empty range opens within its bounds and cannot apply missing endpoints'
  );
  day('2028-02-29').click();
  day('2028-02-29').click();
  apply.click();
  check(
    range.value.from === '2028-02-29' && range.value.to === '2028-02-29' && changes === 2,
    'two selections can apply a one-day range at both bounds'
  );
  trigger.click();
  range.remove();
  check(!panel.matches(':popover-open'), 'disconnect releases an open range without committing its draft');
}

function pagerContract(fixture: HTMLElement, check: Check): void {
  const pager = create('nexus-pager');
  pager.page = 2;
  pager.pageSize = 20;
  pager.total = 83;
  fixture.append(pager);
  const root = pager.shadowRoot!;
  const buttons = () => [...root.querySelectorAll<HTMLButtonElement>('button')];
  let changes = 0,
    value = 0;
  pager.addEventListener('nexus-change', (event) => {
    changes++;
    value = (event as CustomEvent<{ value: number }>).detail.value;
  });
  check(
    root.querySelector('.summary')!.textContent!.includes('83') && root.textContent!.includes('21–40'),
    'known-total pagination displays the item total and actual page range'
  );
  check(
    buttons().filter((button) => /^\d+$/.test(button.textContent!)).length === 5,
    'small pagers expose every numeric page'
  );
  root.querySelector<HTMLButtonElement>('button[data-page="5"]')!.click();
  check(
    changes === 1 && value === 5 && pager.page === 2,
    'a numeric jump emits one request while the consumer owns the current page'
  );
  pager.page = 5;
  check(
    root.querySelector('[aria-current=page]')!.textContent === '5' &&
      buttons().at(-1)!.disabled &&
      root.textContent!.includes('81–83'),
    'the final page displays its partial range and disables Next'
  );
  pager.total = null;
  pager.page = 9;
  pager.hasNext = true;
  check(
    !root.textContent!.includes('83') &&
      !root.textContent!.includes('/') &&
      root.querySelector('[aria-current=page]')!.textContent === '9',
    'unknown-total pagination identifies the current page without fabricating an item or page total'
  );
  buttons().at(-1)!.click();
  check(changes === 2 && value === 10, 'an unknown-total pager can request the next advertised page');
  pager.hasNext = false;
  check(buttons().at(-1)!.disabled, 'unknown-total pagination honors the stable cursor end');
  pager.total = 500000;
  pager.page = 5000;
  check(
    buttons().length <= 9 &&
      root.querySelector('button[data-page="1"]') &&
      root.querySelector('button[data-page="25000"]'),
    'large known totals keep bounded page controls while retaining first and last jumps'
  );
  pager.focus();
  check(
    root.activeElement?.getAttribute('aria-current') === 'page',
    'pager focus reaches the current numeric control'
  );
  pager.total = 0;
  check(pager.hidden, 'a single empty page does not display a redundant pager');
  pager.remove();
}
