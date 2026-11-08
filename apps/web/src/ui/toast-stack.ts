const toasts = new Set<HTMLElement>();

function arrange(): void {
  let bottom = 24;
  for (const toast of [...toasts].reverse()) {
    toast.style.setProperty('--nx-toast-bottom', bottom + 'px');
    if (toast.getClientRects().length) bottom += toast.getBoundingClientRect().height + 12;
  }
}

export function joinToastStack(toast: HTMLElement, signal: AbortSignal): void {
  toasts.add(toast);
  const observer = new ResizeObserver(arrange);
  observer.observe(toast);
  arrange();
  signal.addEventListener('abort', () => {
    observer.disconnect();
    toasts.delete(toast);
    arrange();
  }, { once: true });
}
