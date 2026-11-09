import type { UiElement } from '@nexuspipeline/extension-sdk';
import { validateForm } from '../../apps/web/src/components/form-validation';
import theme from '../../apps/web/src/styles/theme.css?inline';
import controls from '../../apps/web/src/styles/painted-controls.css?inline';

export async function validationLayoutContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const style = document.createElement('style'); style.textContent = theme + controls; fixture.append(style);
  const modal = document.createElement('nexus-modal') as UiElement<'nexus-modal'>;
  modal.title = 'Validation'; modal.open = true;
  const form = document.createElement('form'); form.noValidate = true;
  const text = document.createElement('input'); text.required = true;
  const custom = document.createElement('nexus-text-input') as UiElement<'nexus-text-input'>;
  custom.setAttribute('aria-required', 'true');
  const path = document.createElement('nexus-path-picker') as UiElement<'nexus-path-picker'>;
  path.setAttribute('aria-required', 'true');
  const last = document.createElement('button'); last.type = 'button'; last.textContent = 'Last action';
  const spacer = document.createElement('div'); spacer.style.height = '900px';
  form.append(text, custom, path, spacer, last); modal.append(form);
  const footer = document.createElement('button'); footer.slot = 'footer'; footer.textContent = 'Save'; modal.append(footer);
  fixture.append(modal);
  let invalid = 0, saved = 0;
  form.addEventListener('invalid', () => invalid++, true);
  form.addEventListener('submit', event => { event.preventDefault(); if (validateForm(form)) saved++; });
  try {
    form.requestSubmit();
    custom.placeholder = 'Required field';
    check(!saved && !invalid && text.getAttribute('aria-invalid') === 'true' &&
      custom.shadowRoot!.querySelector('input')!.getAttribute('aria-invalid') === 'true' &&
      path.shadowRoot!.querySelector('input')!.getAttribute('aria-invalid') === 'true',
      'empty fields retain their invalid state across control redraws without native validation bubbles or a write');
    check(document.querySelector('nexus-toast')?.matches(':popover-open'), 'invalid save displays a top-layer toast');
    check(document.activeElement === text, 'invalid save focuses the first field requiring correction');
    text.value = 'Name'; text.dispatchEvent(new Event('input')); custom.value = 'Root'; path.value = 'D:/Scripts';
    form.requestSubmit(); check(saved === 1, 'corrected required fields permit one save');
    const area = modal.shadowRoot!.querySelector('nexus-scroll-area')!;
    await new Promise(resolve => setTimeout(resolve, 260));
    area.scrollTop = 10000;
    await new Promise(resolve => requestAnimationFrame(resolve));
    const viewport = area.shadowRoot!.querySelector('.viewport')!.getBoundingClientRect();
    const end = last.getBoundingClientRect();
    check(end.bottom < viewport.bottom && end.top >= viewport.top,
      'scrolling a modal to its end leaves padding after the final complete action');
    last.focus();
    check(document.activeElement === last, 'the final complete action remains keyboard focusable');
  } finally {
    modal.remove();
    style.remove();
    document.querySelector('nexus-toast')?.dispatchEvent(new CustomEvent('nexus-close'));
  }
}
