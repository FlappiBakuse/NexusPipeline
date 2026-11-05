const documents = new WeakMap<Document, {
  listeners: Set<() => void>;
  observer: MutationObserver;
}>();

export function observeLocale(document: Document, signal: AbortSignal, listener: () => void): void {
  if (signal.aborted) return;
  let state = documents.get(document);
  if (!state) {
    const listeners = new Set<() => void>();
    const observer = new MutationObserver(() => {
      for (const refresh of listeners) refresh();
    });
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['lang'] });
    state = { listeners, observer };
    documents.set(document, state);
  }
  const owner = state;
  owner.listeners.add(listener);
  signal.addEventListener('abort', () => {
    owner.listeners.delete(listener);
    if (!owner.listeners.size) {
      owner.observer.disconnect();
      documents.delete(document);
    }
  }, { once: true });
}
