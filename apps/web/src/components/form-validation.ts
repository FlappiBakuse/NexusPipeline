import type { UiElement } from '@nexuspipeline/extension-sdk';
import { t } from '../app/locale';

const observed = new WeakSet<HTMLElement>();
let toast: UiElement<'nexus-toast'> | undefined;
function mark(element: HTMLElement, valid: boolean): void {
  element.setAttribute('aria-invalid', String(!valid));
  const tree = element.getRootNode();
  if (tree instanceof ShadowRoot && ['nexus-text-input', 'nexus-text-area', 'nexus-number-input', 'nexus-path-picker'].includes(tree.host.localName))
    tree.host.setAttribute('aria-invalid', String(!valid));
}

export function validateForm(root: HTMLElement | undefined): boolean {
  if (!root) return true;
  let first: HTMLElement | undefined;
  const visit = (container: HTMLElement | ShadowRoot): void => {
    for (const element of container.querySelectorAll<HTMLElement>('*')) {
      if (element.closest('[inert], :disabled') || element.hidden || !element.getClientRects().length) continue;
      if (element.localName === 'nexus-select' && element.getAttribute('aria-required') === 'true') {
        const valid = () => {
          const value = (element as UiElement<'nexus-select'>).value;
          return Array.isArray(value) ? value.length > 0 : !!value;
        };
        mark(element, valid());
        if (!observed.has(element)) {
          observed.add(element);
          element.addEventListener('nexus-change', () => mark(element, valid()));
        }
      }
      if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement || element instanceof HTMLSelectElement) {
        if (element.disabled || 'readOnly' in element && element.readOnly) continue;
        if (element instanceof HTMLInputElement && element.type === 'number')
          element.dispatchEvent(new Event('change', { bubbles: true }));
        mark(element, element.validity.valid);
        if (!element.validity.valid) first ??= element;
        if (!observed.has(element)) {
          observed.add(element);
          element.addEventListener('input', () => mark(element, element.validity.valid));
        }
      }
      if (element.shadowRoot) visit(element.shadowRoot);
      if (element.localName.startsWith('nexus-') && element.getAttribute('aria-invalid') === 'true')
        first ??= element;
    }
  };
  visit(root);
  if (!first) return true;
  first.focus();
  if (!toast?.isConnected) {
    toast = document.createElement('nexus-toast') as UiElement<'nexus-toast'>;
    toast.tone = 'danger';
    toast.addEventListener('nexus-close', () => { toast?.remove(); toast = undefined; }, { once: true });
    document.body.append(toast);
  }
  toast.message = '';
  toast.message = t('请修正无效字段后再保存。');
  return false;
}
