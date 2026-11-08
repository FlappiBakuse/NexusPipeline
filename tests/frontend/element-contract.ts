import { contractFingerprint, type UiElement, type UiElementName } from '@nexuspipeline/extension-sdk';
import { registerElements } from '../../apps/web/src/ui/register-elements.js';
import { registryContracts } from './registry-contract.js';
import { paintedControls } from './painted-controls-contract.js';
import { localeContract } from './locale-contract.js';
import { recordWindowContract } from './record-window-contract.js';
import { interfaceCommandContract } from './interface-command-contract.js';
import { desktopCaptionContract } from './desktop-caption-contract.js';
import { scrollAreaContract } from './scroll-area-contract.js';
import { validationLayoutContract } from './validation-layout-contract.js';
import { tooltipContract } from './tooltip-contract.js';
import { modalOwnershipContract } from './modal-ownership-contract.js';
import { adapterFormContract } from './adapter-form-contract.js';
import { historyLinkContract } from './history-link-contract.js';
import { historyIndexContract } from './history-index-contract.js';
import { historyPaginationContract } from './history-pagination-contract.js';
import { historyCatalogContract } from './history-catalog-contract.js';
import { bindingLayoutContract } from './binding-layout-contract.js';
import { scriptCreationContract } from './script-creation-contract.js';
import { settingsCategoryContract } from './settings-category-contract.js';
import { preparationContract } from './preparation-contract.js';
import { loaderContract } from './loader-contract.js';
import { frontendLoadingContract } from './frontend-loading-contract.js';
import { startupAppearanceContract } from './startup-appearance-contract.js';
import { pageLoadingContract } from './page-loading-contract.js';

const checks: string[] = [];
const create = <N extends UiElementName>(name: N): UiElement<N> => document.createElement(name) as UiElement<N>;
function assert(condition: unknown, name: string): asserts condition {
  if (!condition) throw new Error(name);
  checks.push(name);
}
function control(element: HTMLElement): HTMLElement {
  return element.shadowRoot!.querySelector<HTMLElement>('input,button')!;
}
const fixture = document.createElement('main');
fixture.style.setProperty('--nx-color-text', 'rgb(210, 220, 230)');
document.body.append(fixture);
let keyboardSwitch: UiElement<'nexus-switch'>;
let switchChanges = 0;
let switchKeyEvents = 0;
let switchNativeClicks = 0;
let ime: UiElement<'nexus-text-input'>;
let imeChanges = 0;
let imeCompositionEvents = 0;
let outer: UiElement<'nexus-modal'>;
let inner: UiElement<'nexus-modal'>;
let outerClose = 0;
let innerClose = 0;
let returnFocus: UiElement<'nexus-button'>;
let accessibilityFixture: HTMLElement;

import { feedbackLayoutContract } from './feedback-layout-contract';

async function structural(): Promise<void> {
  registerElements();
  paintedControls(fixture, assert);
  await localeContract(fixture, assert);
  desktopCaptionContract(fixture, assert);
  modalOwnershipContract(fixture, assert);
  await scrollAreaContract(fixture, assert);
  await validationLayoutContract(fixture, assert);
  await tooltipContract(fixture, assert);
  await recordWindowContract(assert);
  await adapterFormContract(fixture, assert);
  await historyLinkContract(assert);
  await historyIndexContract(fixture, assert);
  await historyPaginationContract(fixture, assert);
  await historyCatalogContract(fixture, assert);
  await settingsCategoryContract(fixture, assert);
  await frontendLoadingContract(assert);
  await startupAppearanceContract(fixture, assert);
  await preparationContract(assert);
  await loaderContract(assert);
  await pageLoadingContract(fixture, assert);
  await scriptCreationContract(fixture, assert);
  await bindingLayoutContract(fixture, assert);
  await feedbackLayoutContract(fixture, assert);
  await interfaceCommandContract(assert);
  assert(!customElements.get('nxp-button'), 'new realm registers no retired element alias');
  const button = create('nexus-button');
  button.label = 'Action';
  const icon = document.createElement('span');
  icon.slot = 'icon';
  icon.textContent = '+';
  button.append(icon);
  fixture.append(button);
  const nativeButton = control(button);
  let clicks = 0;
  button.addEventListener('click', () => { clicks++; });
  nativeButton.click();
  assert(clicks === 1, 'button forwards exactly one native click');
  button.busy = true;
  nativeButton.click();
  assert(clicks === 1 && button.hasAttribute('busy'), 'busy button blocks action and reflects boolean');
  button.removeAttribute('busy');
  assert(!button.busy, 'boolean attribute removal updates property');
  assert(button.shadowRoot!.querySelector<HTMLSlotElement>('slot[name=icon]')!.assignedElements()[0] === icon,
    'named slot retains actual consumer element identity');
  button.label = 'Changed';
  assert(control(button) === nativeButton, 'property updates preserve native control identity');
  assert(getComputedStyle(nativeButton).color === 'rgb(210, 220, 230)', 'theme tokens inherit through shadow root');
  button.focus();
  assert(document.activeElement === button && button.shadowRoot!.activeElement === nativeButton, 'public focus reaches actual button');

  const form = document.createElement('form');
  const label = document.createElement('label');
  label.htmlFor = 'secret-input';
  label.textContent = 'Secret';
  const input = create('nexus-text-input');
  input.id = 'secret-input';
  input.setAttribute('name', 'credential');
  input.type = 'password';
  input.value = 'fixture-only-secret';
  let changes = 0;
  input.addEventListener('nexus-change', () => { changes++; });
  form.append(label, input);
  fixture.append(form);
  assert(new FormData(form).get('credential') === 'fixture-only-secret', 'form association submits current property value');
  assert(!input.hasAttribute('value') && changes === 0, 'password stays off attributes and programmatic value emits no change');
  assert(control(input).getAttribute('aria-label') === 'Secret', 'form label names the actual input');
  input.readOnly = true;
  input.disabled = true;
  assert((control(input) as HTMLInputElement).readOnly && (control(input) as HTMLInputElement).disabled,
    'readonly and disabled reach actual input');
  input.value = '';
  form.remove();

  const tabs = create('nexus-tabs');
  tabs.items = [{ id: 'first', label: 'First' }, { id: 'second', label: 'Second' }];
  tabs.selectedId = 'first';
  const first = document.createElement('section');
  const second = document.createElement('section');
  first.slot = second.slot = 'panels';
  first.dataset.tabId = 'first';
  second.dataset.tabId = 'second';
  tabs.append(first, second);
  fixture.append(tabs);
  await new Promise<void>(resolve => queueMicrotask(resolve));
  assert(!first.hidden && second.hidden && second.inert, 'tabs retain panel identity and prevent hidden interaction');
  assert(tabs.shadowRoot!.querySelector('button')!.ariaControlsElements?.[0] === first && first.getAttribute('aria-label') === 'First',
    'tab controls the slotted panel and panel has an accessible name');
  tabs.selectedId = 'second';
  assert(first.hidden && !second.hidden, 'controlled tab property changes visible panel');
  let rejected = false;
  try { tabs.items = [{ id: 'duplicate', label: 'A' }, { id: 'duplicate', label: 'B' }]; } catch { rejected = true; }
  assert(rejected, 'duplicate tab ids are rejected');
  tabs.remove();
  await registryContracts(checks);
}

async function prepareAccessibility(): Promise<void> {
  accessibilityFixture = document.createElement('section');
  const label = document.createElement('label');
  label.htmlFor = 'accessible-input';
  label.textContent = 'Account name';
  const description = document.createElement('p');
  description.id = 'accessible-description';
  description.textContent = 'Description example';
  const input = create('nexus-text-input');
  input.id = 'accessible-input';
  input.setAttribute('aria-describedby', description.id);
  const tabs = create('nexus-tabs');
  tabs.items = [{ id: 'one', label: 'Accessible tab' }];
  tabs.selectedId = 'one';
  const panel = document.createElement('section');
  panel.slot = 'panels';
  panel.dataset.tabId = 'one';
  panel.textContent = 'Panel content';
  tabs.append(panel);
  accessibilityFixture.append(label, description, input, tabs);
  fixture.append(accessibilityFixture);
  await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
}
function verifyAccessibility(names: readonly string[]): void {
  assert(['tab:Accessible tab', 'tabpanel:Accessible tab', 'textbox:Account name:Description example']
    .every(name => names.includes(name)), 'browser accessibility tree exposes tab panel label and input description');
  accessibilityFixture.remove();
}

function prepareKeyboardSwitch(): void {
  keyboardSwitch = create('nexus-switch');
  keyboardSwitch.setAttribute('aria-label', 'Enabled');
  fixture.append(keyboardSwitch);
  control(keyboardSwitch).addEventListener('keydown', event => { if (event.isTrusted) switchKeyEvents++; });
  control(keyboardSwitch).addEventListener('click', event => { if (event.isTrusted) switchNativeClicks++; });
  keyboardSwitch.addEventListener('nexus-change', event => {
    const change = event as CustomEvent;
    if (!change.bubbles || !change.composed || !Object.isFrozen(change.detail) || change.detail.source !== 'keyboard') {
      throw new Error('Invalid public switch event');
    }
    switchChanges++;
  });
  keyboardSwitch.focus();
}
function verifyKeyboardSwitch(): void {
  if (!keyboardSwitch.value || switchChanges !== 1) {
    throw new Error('Keyboard switch state: ' + JSON.stringify({ value: keyboardSwitch.value, changes: switchChanges,
      trustedKeyEvents: switchKeyEvents, trustedClicks: switchNativeClicks,
      hostFocused: document.activeElement === keyboardSwitch,
      controlFocused: keyboardSwitch.shadowRoot!.activeElement === control(keyboardSwitch),
      documentFocused: document.hasFocus(), disabled: (control(keyboardSwitch) as HTMLButtonElement).disabled }));
  }
  assert(keyboardSwitch.value && switchChanges === 1, 'trusted Space toggles once with readonly composed keyboard event');
  keyboardSwitch.remove();
  fixture.append(keyboardSwitch);
  keyboardSwitch.value = false;
  control(keyboardSwitch).click();
  assert(keyboardSwitch.value && Number(switchChanges) === 2, 'disconnect reconnect does not duplicate listeners');
  keyboardSwitch.remove();
}
function prepareComposition(): void {
  ime = create('nexus-text-input');
  ime.setAttribute('aria-label', 'Composition input');
  ime.addEventListener('nexus-change', () => { imeChanges++; });
  fixture.append(ime);
  control(ime).addEventListener('compositionstart', event => { if (event.isTrusted) imeCompositionEvents++; });
  ime.focus();
}
function verifyCompositionPending(): void {
  assert(imeChanges === 0 && imeCompositionEvents === 1, 'trusted IME composition defers public change');
}
function verifyCompositionCommitted(): void {
  assert(ime.value === '测试' && imeChanges === 1, 'trusted IME commit emits exactly one final value');
  ime.remove();
}
function prepareDialogs(): void {
  const language = document.documentElement.lang;
  document.documentElement.lang = 'zh-CN';
  const confirm = create('nexus-confirm-dialog');
  fixture.append(confirm);
  assert(confirm.shadowRoot!.querySelector('[data-action="cancel"]')!.textContent === '取消'
    && confirm.shadowRoot!.querySelector('[data-action="confirm"]')!.textContent === '确认', 'confirmation actions follow Chinese document language');
  document.documentElement.lang = 'en-US';
  (confirm as unknown as { title: string }).title = 'Confirm';
  assert(confirm.shadowRoot!.querySelector('[data-action="cancel"]')!.textContent === 'Cancel'
    && confirm.shadowRoot!.querySelector('[data-action="confirm"]')!.textContent === 'Confirm', 'confirmation actions follow English document language');
  confirm.remove(); document.documentElement.lang = language;
  returnFocus = create('nexus-button');
  returnFocus.label = 'Open';
  outer = create('nexus-modal');
  inner = create('nexus-modal');
  outer.surface = 'secondary';
  outer.title = 'Outer';
  inner.title = 'Inner';
  const nested = document.createElement('div');
  nested.append(inner);
  outer.append(nested);
  fixture.append(returnFocus, outer);
  returnFocus.focus();
  outer.addEventListener('nexus-close', event => { if (event.target === outer) outerClose++; });
  inner.addEventListener('nexus-close', event => {
    if ((event as CustomEvent).detail.reason === 'escape') innerClose++;
    inner.open = false;
  });
  outer.open = true;
  assert(outer.surface === 'secondary' && outer.shadowRoot!.querySelector('dialog')!.dataset.surface === 'secondary',
    'pre-connect public surface property preserves modal lifecycle and selected appearance');
  inner.open = true;
}
async function verifyDialogEscape(): Promise<void> {
  assert(innerClose === 1 && outerClose === 0 && outer.open && !inner.open, 'trusted Escape requests only the top dialog close');
  outer.open = false;
  const closing = outer.shadowRoot!.querySelector('dialog')!.getAnimations({ subtree: true });
  assert(outer.shadowRoot!.querySelector('dialog')!.open && closing.some(animation => animation.effect?.getTiming().duration === 200),
    'modal keeps its exit animation before releasing the top layer');
  await Promise.all(closing.map(animation => animation.finished.catch(() => {})));
  await new Promise(resolve => setTimeout(resolve, 0));
  assert(document.activeElement === returnFocus, 'closing nested dialogs restores original focus');
  const dialog = outer.shadowRoot!.querySelector('dialog')!;
  outer.open = true;
  const animations = dialog.getAnimations({ subtree: true });
  assert(animations.some(animation => animation.effect?.getTiming().duration === 200), 'modal retains visible opening animation');
  outer.remove();
  assert(!dialog.open, 'disconnect closes top layer dialog');
}
function finish(): object {
  return { checks, checkCount: checks.length, contractFingerprint, scope: 'Native element ABI and frontend activation fixture' };
}
Object.assign(globalThis, { nexusUiTests: { structural, prepareKeyboardSwitch, verifyKeyboardSwitch, prepareComposition,
  verifyCompositionPending, verifyCompositionCommitted, prepareDialogs, verifyDialogEscape, prepareAccessibility, verifyAccessibility, finish } });
