export function suppressNativeValidation(event: Event): void {
  event.preventDefault();
}

export function suppressNativeControlTooltips(root: ParentNode): void {
  for (const control of root.querySelectorAll('input,textarea,select')) control.setAttribute('title', '');
}

function suppressFocusedControlTooltip(event: Event): void {
  const control = event.composedPath()[0];
  if (control instanceof HTMLInputElement || control instanceof HTMLTextAreaElement || control instanceof HTMLSelectElement)
    control.setAttribute('title', '');
}

export function installValidationPresentation(document: Document): void {
  // Chromium uses validationMessage when the flat tree has no title; an empty title blocks that fallback.
  document.documentElement.setAttribute('title', '');
  document.addEventListener('invalid', suppressNativeValidation, true);
  document.addEventListener('focusin', suppressFocusedControlTooltip, true);
}
