import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';

type Move = (ids: string[]) => void;

function attachSortablePointer(list: HTMLElement, signal: AbortSignal,
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

export class DragHandle extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-drag-handle');
  private readonly button: HTMLButtonElement;
  constructor() {
    super(
      'nexus-drag-handle',
      controlStyles +
        'button{min-width:var(--nx-drag-size,24px);width:var(--nx-drag-size,24px);min-height:var(--nx-drag-size,24px);height:var(--nx-drag-size,24px);padding:0;border:0;box-shadow:none;background:transparent;color:var(--nx-color-muted);cursor:grab;touch-action:none}button:active{cursor:grabbing}',
      '<button type="button" aria-keyshortcuts="ArrowUp ArrowDown" draggable="false"><svg width="10" height="14" viewBox="0 0 10 14" aria-hidden="true"><g fill="currentColor"><circle cx="3" cy="3" r="1.2"/><circle cx="7" cy="3" r="1.2"/><circle cx="3" cy="7" r="1.2"/><circle cx="7" cy="7" r="1.2"/><circle cx="3" cy="11" r="1.2"/><circle cx="7" cy="11" r="1.2"/></g></svg></button>'
    );
    this.button = this.root.querySelector('button')!;
  }
  protected connect(signal: AbortSignal): void {
    this.button.addEventListener(
      'keydown',
      (event) => {
        if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
        event.preventDefault();
        this.emit('nexus-action', {
          actionId: `${this.values.itemId}:${event.key === 'ArrowUp' ? 'up' : 'down'}`,
          source: 'keyboard'
        });
      },
      { signal }
    );
  }
  override focus(options?: FocusOptions): void {
    this.button.focus(options);
  }
  protected render(): void {
    this.button.disabled = Boolean(this.values.disabled);
    this.button.draggable = false;
    this.button.setAttribute('aria-label', String(this.values.label));
  }
}

export class SortableList extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-sortable-list');
  private readonly itemSlot: HTMLSlotElement;
  constructor() {
    super('nexus-sortable-list', ':host{display:block}::slotted(*){display:block}::slotted([data-dragging]){box-shadow:0 12px 24px #0005;opacity:.92}::slotted([data-drop-target]){outline:2px solid var(--nx-color-accent);outline-offset:2px}', '<slot></slot>');
    this.itemSlot = this.root.querySelector('slot')!;
  }
  protected connect(signal: AbortSignal): void {
    this.addEventListener(
      'nexus-action',
      (event) => {
        if (this.values.disabled) return;
        const action = (event as CustomEvent<{ actionId: string }>).detail.actionId;
        const [id, direction] = action.split(':');
        if (direction !== 'up' && direction !== 'down') return;
        event.stopPropagation();
        this.move(id!, direction === 'up' ? -1 : 1, 'keyboard');
      },
      { signal }
    );
    attachSortablePointer(this, signal, () => this.values.ids as string[], () => Boolean(this.values.disabled), ids => {
      this.emit('nexus-reorder', { ids: Object.freeze(ids), source: 'pointer' });
    });
  }
  protected render(): void {
    const ids = this.values.ids as readonly string[];
    this.setAttribute('role', 'list');
    this.toggleAttribute('data-reorder-disabled', Boolean(this.values.disabled));
    this.itemSlot.assignedElements().forEach((element) => {
      const id = element.getAttribute('data-item-id');
      element.toggleAttribute('data-sortable', !this.values.disabled && Boolean(id && ids.includes(id)));
      if (id && ids.includes(id)) element.setAttribute('role', 'listitem');
    });
  }
  private move(id: string, delta: number, source: 'keyboard'): void {
    const ids = [...(this.values.ids as string[])];
    const index = ids.indexOf(id),
      next = index + delta;
    if (index < 0 || next < 0 || next >= ids.length) return;
    [ids[index], ids[next]] = [ids[next]!, ids[index]!];
    this.emit('nexus-reorder', { ids: Object.freeze(ids), source });
  }
}
