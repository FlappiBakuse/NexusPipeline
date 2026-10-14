import { onBeforeUnmount, onMounted, watch, type Ref, type Directive } from "vue";

type Surface = { element: HTMLElement; flatten: () => boolean };
const surfaces = new Map<HTMLElement, Surface>();
const dirty = new Set<Surface>();
let observer: MutationObserver | undefined;
let scheduled = false;

function refresh() {
  scheduled = false;
  const ordered = [...dirty].filter(surface => surfaces.get(surface.element) === surface && surface.element.isConnected);
  dirty.clear();
  ordered.sort((a, b) => a.element === b.element ? 0 : a.element.compareDocumentPosition(b.element) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1);
  for (const { element, flatten } of ordered) {
    let depth = 0;
    let parentTone = "b";
    let foundTone = false;
    for (let parent = element.parentElement; parent; parent = parent.parentElement) {
      if (parent.hasAttribute("data-nxp-surface-context")) break;
      if (parent.dataset.nxpSurfaceKind !== "card") continue;
      depth++;
      if (!foundTone) { parentTone = parent.dataset.nxpSurfaceTone || "a"; foundTone = true; }
    }
    const group = flatten() && depth >= 3;
    element.dataset.nxpSurfaceKind = group ? "group" : "card";
    element.dataset.nxpSurfaceDepth = String(group ? depth : depth + 1);
    element.dataset.nxpSurfaceTone = group ? parentTone : parentTone === "a" ? "b" : "a";
  }
}

function schedule() {
  if (scheduled || !dirty.size) return;
  scheduled = true;
  queueMicrotask(refresh);
}

function invalidateSubtree(node: Node) {
  if (!(node instanceof Element)) return;
  const active = surfaces.get(node as HTMLElement);
  if (active) dirty.add(active);
  for (const element of node.querySelectorAll<HTMLElement>("[data-nxp-surface-kind]")) {
    const descendant = surfaces.get(element);
    if (descendant) dirty.add(descendant);
  }
  schedule();
}

function registerSurface(element: HTMLElement, flatten: () => boolean = () => false) {
  const active = { element, flatten };
  surfaces.set(element, active);
  // Detached registrations must be discoverable when their containing subtree is inserted later.
  element.dataset.nxpSurfaceKind = "card";
  // Public light-DOM elements have separate Vue apps, so ancestry follows the rendered tree.
  if (!observer) {
    observer = new MutationObserver(records => {
      for (const record of records) {
        for (const node of record.addedNodes) invalidateSubtree(node);
        for (const node of record.removedNodes) invalidateSubtree(node);
      }
    });
    observer.observe(document.documentElement, { childList: true, subtree: true });
  }
  invalidateSubtree(element);
  return () => {
    surfaces.delete(element);
    dirty.delete(active);
    delete element.dataset.nxpSurfaceKind;
    delete element.dataset.nxpSurfaceDepth;
    delete element.dataset.nxpSurfaceTone;
    if (!surfaces.size) { observer?.disconnect(); observer = undefined; }
    else invalidateSubtree(element);
  };
}

const registrations = new WeakMap<HTMLElement, () => void>();
export const vNxpSurface: Directive<HTMLElement> = {
  mounted(element) { registrations.set(element, registerSurface(element)); },
  unmounted(element) { registrations.get(element)?.(); registrations.delete(element); },
};

export function useSurface(root: Ref<HTMLElement | null>, flatten: () => boolean = () => false) {
  let release: (() => void) | undefined;
  function register() {
    release?.();
    release = root.value ? registerSurface(root.value, flatten) : undefined;
  }
  onMounted(register);
  watch(root, register, { flush: "post" });
  watch(flatten, () => { if (root.value) invalidateSubtree(root.value); });
  onBeforeUnmount(() => release?.());
}
