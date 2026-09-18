import { nextTick, type Directive } from "vue";

export interface SortableOptions {
  axis?: "y" | "both";
  disabled?: boolean;
  canDrag?: (item: HTMLElement) => boolean;
  onDrop?: (ids: string[], movedId: string) => void | Promise<void>;
}

interface SortableState {
  element: HTMLElement;
  options: SortableOptions;
  onKeydown: (event: KeyboardEvent) => void;
  onPointerDown: (event: PointerEvent) => void;
  onPointerMove: (event: PointerEvent) => void;
  onPointerUp: (event: PointerEvent) => void;
  onPointerCancel: (event: PointerEvent) => void;
  observer: MutationObserver;
}

interface ActiveDrag {
  container: HTMLElement;
  item: HTMLElement;
  pointerId: number;
  axis: "y" | "both";
  startX: number;
  startY: number;
  startScrollX: number;
  startScrollY: number;
  initialOrder: string[];
  moved: boolean;
  placeBefore: HTMLElement | null;
  previousTransform: string;
  previousZIndex: string;
  previousWillChange: string;
}

const states = new WeakMap<HTMLElement, SortableState>();
let activeDrag: ActiveDrag | null = null;

function directItems(container: HTMLElement): HTMLElement[] {
  return Array.from(container.children).filter(
    (child): child is HTMLElement => child instanceof HTMLElement && child.matches("[data-dnd-id]"),
  );
}

function itemIds(container: HTMLElement): string[] {
  return directItems(container)
    .map(item => item.dataset.dndId || "")
    .filter(Boolean);
}

function validItems(container: HTMLElement): boolean {
  const ids = itemIds(container);
  return ids.length === directItems(container).length && new Set(ids).size === ids.length;
}

function reportOrder(container: HTMLElement, ids: string[], item: HTMLElement, options: SortableOptions) {
  const focused = document.activeElement;
  const restoreFocus = async () => {
    await nextTick();
    if (focused instanceof HTMLElement && focused.isConnected && item.contains(focused)) focused.focus();
  };
  try {
    const result = options.onDrop?.(ids, item.dataset.dndId || "");
    void Promise.resolve(result).then(restoreFocus, error => { console.error("Sortable reorder failed", error); return restoreFocus(); });
  } catch (error) { void restoreFocus(); throw error; }
}

function sortableOwner(target: Element | null): HTMLElement | null {
  let current: Element | null = target;
  while (current) {
    if (current instanceof HTMLElement && states.has(current)) return current;
    current = current.parentElement;
  }
  return null;
}

function dragHandle(target: EventTarget | null): HTMLElement | null {
  return target instanceof Element ? target.closest<HTMLElement>("[data-drag-handle]") : null;
}

function sortableItem(container: HTMLElement, handle: Element | null): HTMLElement | null {
  if (!handle || sortableOwner(handle) !== container) return null;
  let current: HTMLElement | null = handle instanceof HTMLElement ? handle : handle.parentElement;
  while (current && current !== container) {
    if (current.parentElement === container && current.matches("[data-dnd-id]")) return current;
    current = current.parentElement;
  }
  return null;
}

function isDisabled(handle: Element): boolean {
  return handle.getAttribute("aria-disabled") === "true" || handle.hasAttribute("disabled");
}

function placeBeforeForPoint(drag: ActiveDrag, clientX: number, clientY: number): HTMLElement | null {
  const candidates = directItems(drag.container).filter(item => item !== drag.item);
  if (drag.axis === "both") {
    let closest: HTMLElement | null = null;
    let closestDistance = Number.POSITIVE_INFINITY;
    for (const item of candidates) {
      const rect = item.getBoundingClientRect();
      const centerX = rect.left + rect.width / 2;
      const centerY = rect.top + rect.height / 2;
      const distance = Math.hypot(clientX - centerX, clientY - centerY);
      if (distance < closestDistance) {
        closest = item;
        closestDistance = distance;
      }
    }
    if (!closest) return null;
    const rect = closest.getBoundingClientRect();
    const before = clientY < rect.top + rect.height / 2
      || (Math.abs(clientY - (rect.top + rect.height / 2)) <= rect.height / 2 && clientX < rect.left + rect.width / 2);
    if (before) return closest;
    const index = directItems(drag.container).indexOf(closest);
    return directItems(drag.container)[index + 1] || null;
  }
  return candidates.find(item => {
    const rect = item.getBoundingClientRect();
    return clientY < rect.top + rect.height / 2;
  }) || null;
}

function clearDropMarker(drag: ActiveDrag) {
  directItems(drag.container).forEach(item => item.classList.remove("dnd-drop-before"));
}

function finishPointerDrag(commit: boolean) {
  const drag = activeDrag;
  if (!drag) return;
  activeDrag = null;
  clearDropMarker(drag);
  drag.container.classList.remove("dnd-active");
  if (drag.container.hasPointerCapture?.(drag.pointerId)) drag.container.releasePointerCapture(drag.pointerId);

  const state = states.get(drag.container);
  if (commit && drag.moved && drag.placeBefore !== drag.item && !state?.options.disabled
    && validItems(drag.container) && itemIds(drag.container).join("\u0000") === drag.initialOrder.join("\u0000")) {
    const items = directItems(drag.container).filter(item => item !== drag.item);
    const index = drag.placeBefore ? items.indexOf(drag.placeBefore) : items.length;
    if (index >= 0) items.splice(index, 0, drag.item);
    const ids = items.map(item => item.dataset.dndId!);
    if (ids.join("\u0000") !== drag.initialOrder.join("\u0000")) {
      reportOrder(drag.container, ids, drag.item, state?.options || {});
    }
  }

  drag.item.classList.remove("dnd-dragging");
  drag.item.style.transform = drag.previousTransform;
  drag.item.style.zIndex = drag.previousZIndex;
  drag.item.style.willChange = drag.previousWillChange;
}

function handlePointerMove(event: PointerEvent) {
  const drag = activeDrag;
  if (!drag || event.pointerId !== drag.pointerId) return;
  const scrollX = typeof window === "undefined" ? 0 : window.scrollX;
  const scrollY = typeof window === "undefined" ? 0 : window.scrollY;
  const deltaX = event.clientX - drag.startX + scrollX - drag.startScrollX;
  const deltaY = event.clientY - drag.startY + scrollY - drag.startScrollY;
  if (!drag.moved && Math.hypot(deltaX, deltaY) < 4) return;
  drag.moved = true;
  const x = drag.axis === "both" ? deltaX : 0;
  drag.item.style.transform = `translate(${x}px, ${deltaY}px)`;
  const placeBefore = placeBeforeForPoint(drag, event.clientX, event.clientY);
  if (placeBefore !== drag.placeBefore) {
    clearDropMarker(drag);
    placeBefore?.classList.add("dnd-drop-before");
    drag.placeBefore = placeBefore;
  }
  event.preventDefault();
}

function beginPointerDrag(container: HTMLElement, item: HTMLElement, event: PointerEvent, options: SortableOptions) {
  if (activeDrag || options.disabled || !validItems(container) || event.button !== 0 || isDisabled(event.target instanceof Element ? event.target : item)) return;
  if (options.canDrag && !options.canDrag(item)) return;
  activeDrag = {
    container,
    item,
    pointerId: event.pointerId,
    axis: options.axis || "y",
    startX: event.clientX,
    startY: event.clientY,
    startScrollX: typeof window === "undefined" ? 0 : window.scrollX,
    startScrollY: typeof window === "undefined" ? 0 : window.scrollY,
    initialOrder: itemIds(container),
    moved: false,
    placeBefore: null,
    previousTransform: item.style.transform,
    previousZIndex: item.style.zIndex,
    previousWillChange: item.style.willChange,
  };
  item.classList.add("dnd-dragging");
  container.classList.add("dnd-active");
  item.style.willChange = "transform";
  container.setPointerCapture?.(event.pointerId);
  event.preventDefault();
}

function reorderWithKeyboard(container: HTMLElement, item: HTMLElement, direction: -1 | 1, options: SortableOptions) {
  if (options.disabled || !validItems(container)) return;
  if (options.canDrag && !options.canDrag(item)) return;
  const items = directItems(container);
  const index = items.indexOf(item);
  const targetIndex = index + direction;
  if (index < 0 || targetIndex < 0 || targetIndex >= items.length) return;
  items.splice(index, 1);
  items.splice(targetIndex, 0, item);
  reportOrder(container, items.map(value => value.dataset.dndId!), item, options);
}

function mountSortable(element: HTMLElement, options: SortableOptions = {}): SortableState {
  const state: SortableState = {
    element,
    options,
    onPointerDown: event => {
      const handle = dragHandle(event.target);
      if (!handle || isDisabled(handle)) return;
      const item = sortableItem(element, handle);
      if (item) beginPointerDrag(element, item, event, state.options);
    },
    onKeydown: event => {
      if (event.key === "Escape" && activeDrag?.container === element) { finishPointerDrag(false); event.preventDefault(); return; }
      if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
      const handle = dragHandle(event.target);
      if (!handle || isDisabled(handle)) return;
      const item = sortableItem(element, handle);
      if (!item) return;
      event.preventDefault();
      reorderWithKeyboard(element, item, event.key === "ArrowUp" ? -1 : 1, state.options);
    },
    onPointerMove: handlePointerMove,
    onPointerUp: event => { if (activeDrag?.container === element && activeDrag.pointerId === event.pointerId) finishPointerDrag(true); },
    onPointerCancel: event => { if (activeDrag?.container === element && activeDrag.pointerId === event.pointerId) finishPointerDrag(false); },
    observer: new MutationObserver(() => {
      if (activeDrag?.container === element && (!validItems(element) || itemIds(element).join("\u0000") !== activeDrag.initialOrder.join("\u0000"))) finishPointerDrag(false);
    }),
  };
  states.set(element, state);
  state.observer.observe(element, { childList: true, subtree: true, attributes: true, attributeFilter: ["data-dnd-id"] });
  element.dataset.nxpSortable = "true";
  element.addEventListener("pointerdown", state.onPointerDown);
  element.addEventListener("keydown", state.onKeydown);
  element.addEventListener("pointermove", state.onPointerMove);
  element.addEventListener("pointerup", state.onPointerUp);
  element.addEventListener("pointercancel", state.onPointerCancel);
  return state;
}

function unmountSortable(element: HTMLElement) {
  if (activeDrag?.container === element) finishPointerDrag(false);
  const state = states.get(element);
  if (!state) return;
  state.observer.disconnect();
  element.removeEventListener("pointerdown", state.onPointerDown);
  element.removeEventListener("keydown", state.onKeydown);
  element.removeEventListener("pointermove", state.onPointerMove);
  element.removeEventListener("pointerup", state.onPointerUp);
  element.removeEventListener("pointercancel", state.onPointerCancel);
  element.removeAttribute("data-nxp-sortable");
  states.delete(element);
}

export const vSortable: Directive<HTMLElement, SortableOptions> = {
  mounted(element, binding) { mountSortable(element, binding.value); },
  updated(element, binding) {
    const state = states.get(element);
    if (state) {
      state.options = binding.value || {};
      if (state.options.disabled && activeDrag?.container === element) finishPointerDrag(false);
    }
  },
  unmounted(element) { unmountSortable(element); },
};
