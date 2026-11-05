export function validateForm(root: HTMLElement | undefined): boolean {
  if (!root) return true;
  const visit = (container: HTMLElement | ShadowRoot): boolean => {
    for (const element of container.querySelectorAll<HTMLElement>('*')) {
      if (element.closest('[inert]') || element.hidden || !element.getClientRects().length) continue;
      if (element.localName === 'nexus-select' && element.getAttribute('aria-required') === 'true') {
        const value = (element as HTMLElement & { value: string | readonly string[] }).value;
        if (!value || (Array.isArray(value) && !value.length)) {
          element.setAttribute('aria-invalid', 'true');
          element.focus();
          return false;
        }
      }
      if (element.localName.startsWith('nexus-') && element.getAttribute('aria-invalid') === 'true') {
        element.focus();
        return false;
      }
      if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement) {
        if (element.disabled || element.readOnly) continue;
        if (element instanceof HTMLInputElement && element.type === 'number')
          element.dispatchEvent(new Event('change', { bubbles: true }));
        const valid = element.checkValidity();
        element.setAttribute('aria-invalid', String(!valid));
        if (!valid) {
          element.focus();
          return false;
        }
      }
      if (element.shadowRoot && !visit(element.shadowRoot)) return false;
    }
    return true;
  };
  return visit(root);
}
