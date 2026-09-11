type ModalMask = HTMLElement;

function topModalMask() {
  const masks = Array.from(document.querySelectorAll<ModalMask>(".modal-mask"));
  return masks[masks.length - 1] || null;
}

function isLocked(mask: ModalMask) {
  const modal = mask.querySelector<HTMLElement>("[role='dialog'], .modal");
  return mask.dataset.locked !== undefined || modal?.dataset.locked !== undefined;
}

function focusable(mask: ModalMask) {
  return Array.from(
    mask.querySelectorAll<HTMLElement>(
      "button, input, select, textarea, a[href], [tabindex]:not([tabindex='-1'])",
    ),
  ).filter(element => {
    const disabled = element instanceof HTMLButtonElement || element instanceof HTMLInputElement || element instanceof HTMLSelectElement || element instanceof HTMLTextAreaElement
      ? element.disabled
      : false;
    return !disabled && element.getClientRects().length > 0;
  });
}

function focusFirst(mask: ModalMask) {
  const target = focusable(mask)[0];
  if (target && document.activeElement !== target) target.focus({ preventScroll: true });
}

function closeWithButton(mask: ModalMask) {
  const close = mask.querySelector<HTMLButtonElement>(".modal-close");
  close?.click();
}

export function installModalBehavior() {
  let returnFocus: HTMLElement | null = null;
  let focusQueued = false;
  let hadModal = false;

  const queueFocus = () => {
    if (focusQueued) return;
    focusQueued = true;
    queueMicrotask(() => {
      focusQueued = false;
      const mask = topModalMask();
      if (mask) focusFirst(mask);
    });
  };

  const onKeydown = (event: KeyboardEvent) => {
    const mask = topModalMask();
    if (!mask) return;
    const modal = mask.querySelector<HTMLElement>("[role='dialog'], .modal");
    if (!modal) return;
    if (event.key === "Escape") {
      if (isLocked(mask)) {
        event.preventDefault();
        event.stopImmediatePropagation();
        return;
      }
      event.preventDefault();
      event.stopImmediatePropagation();
      closeWithButton(mask);
      return;
    }
    if (event.key !== "Tab") return;
    const elements = focusable(mask);
    if (!elements.length) return;
    if (!modal.contains(document.activeElement)) {
      event.preventDefault();
      elements[0].focus({ preventScroll: true });
      return;
    }
    const first = elements[0];
    const last = elements[elements.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus({ preventScroll: true });
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus({ preventScroll: true });
    }
  };

  const onPointerDown = (event: PointerEvent) => {
    const mask = topModalMask();
    if (!mask || event.target !== mask || isLocked(mask)) return;
    closeWithButton(mask);
  };

  const onFocusIn = (event: FocusEvent) => {
    const mask = topModalMask();
    if (!mask || !(event.target instanceof Node)) return;
    const modal = mask.querySelector<HTMLElement>("[role='dialog'], .modal");
    if (modal && !modal.contains(event.target)) focusFirst(mask);
  };

  const observer = new MutationObserver(() => {
    const mask = topModalMask();
    if (mask && !hadModal) {
      returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      queueFocus();
    } else if (!mask && hadModal) {
      if (returnFocus && document.contains(returnFocus)) returnFocus.focus({ preventScroll: true });
      returnFocus = null;
    } else if (mask) {
      queueFocus();
    }
    hadModal = Boolean(mask);
  });

  window.addEventListener("keydown", onKeydown, true);
  document.addEventListener("pointerdown", onPointerDown, true);
  document.addEventListener("focusin", onFocusIn, true);
  observer.observe(document.body, { childList: true, subtree: true });

  return () => {
    window.removeEventListener("keydown", onKeydown, true);
    document.removeEventListener("pointerdown", onPointerDown, true);
    document.removeEventListener("focusin", onFocusIn, true);
    observer.disconnect();
  };
}
