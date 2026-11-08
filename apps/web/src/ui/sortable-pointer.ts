type Move = (ids: string[]) => void;

export function attachSortablePointer(list: HTMLElement, signal: AbortSignal,
  ids: () => readonly string[], disabled: () => boolean, move: Move): void {
  let drag: { item: HTMLElement; pointer: number; x: number; y: number; left: number; top: number;
    transform: string; zIndex: string; position: string; order: string[]; target: string; moved: boolean } | undefined;
  const items = () => [...list.children].filter((item): item is HTMLElement =>
    item instanceof HTMLElement && ids().includes(item.dataset.itemId || ''));
  const finish = (commit: boolean) => {
    const current = drag; drag = undefined;
    if (!current) return;
    current.item.style.transform = current.transform;
    current.item.style.zIndex = current.zIndex;
    current.item.style.position = current.position;
    current.item.removeAttribute('data-dragging');
    for (const item of items()) item.removeAttribute('data-drop-target');
    if (list.hasPointerCapture(current.pointer)) list.releasePointerCapture(current.pointer);
    if (!commit || disabled() || !current.moved || ids().join('\n') !== current.order.join('\n')) return;
    const from = current.order.indexOf(current.item.dataset.itemId!), to = current.order.indexOf(current.target);
    if (from < 0 || to < 0 || from === to) return;
    const next = [...current.order]; next.splice(from, 1); next.splice(to, 0, current.item.dataset.itemId!);
    move(next);
  };
  list.addEventListener('pointerdown', event => {
    if (event.button !== 0 || disabled()) return;
    const handle = event.composedPath().find(node => node instanceof HTMLElement && node.localName === 'nexus-drag-handle') as HTMLElement | undefined;
    if (!handle || (handle as HTMLElement & { disabled: boolean }).disabled) return;
    const item = items().find(item => item.contains(handle));
    if (!item || handle.closest('nexus-sortable-list') !== list) return;
    finish(false); event.preventDefault(); handle.focus({ preventScroll: true });
    const rect = item.getBoundingClientRect();
    drag = { item, pointer: event.pointerId, x: event.clientX, y: event.clientY, left: rect.left, top: rect.top,
      transform: item.style.transform, zIndex: item.style.zIndex, position: item.style.position,
      order: [...ids()], target: item.dataset.itemId!, moved: false };
    list.setPointerCapture(event.pointerId);
  }, { signal });
  list.addEventListener('pointermove', event => {
    if (!drag || event.pointerId !== drag.pointer) return;
    if (disabled()) { finish(false); return; }
    const dx = event.clientX - drag.x, dy = event.clientY - drag.y;
    if (!drag.moved && Math.hypot(dx, dy) < 4) return;
    drag.moved = true;
    drag.item.style.position = 'relative'; drag.item.style.zIndex = '10';
    drag.item.dataset.dragging = '';
    const current = drag.item.getBoundingClientRect();
    const prior = new DOMMatrix(getComputedStyle(drag.item).transform);
    drag.item.style.transform = `translate(${drag.left + dx - current.left + prior.m41}px,${drag.top + dy - current.top + prior.m42}px)`;
    for (const item of items()) {
      if (item === drag.item) continue;
      const rect = item.getBoundingClientRect();
      if (event.clientX >= rect.left && event.clientX <= rect.right && event.clientY >= rect.top && event.clientY <= rect.bottom)
        drag.target = item.dataset.itemId!;
      item.toggleAttribute('data-drop-target', item.dataset.itemId === drag.target);
    }
  }, { signal });
  list.addEventListener('pointerup', () => finish(true), { signal });
  list.addEventListener('pointercancel', () => finish(false), { signal });
  list.addEventListener('lostpointercapture', () => finish(false), { signal });
  list.ownerDocument.addEventListener('keydown', event => { if (drag && event.key === 'Escape') { event.preventDefault(); finish(false); } }, { signal });
  signal.addEventListener('abort', () => finish(false), { once: true });
}
