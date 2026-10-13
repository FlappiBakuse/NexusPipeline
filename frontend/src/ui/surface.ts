import { onBeforeUnmount, onMounted, watch, type Ref } from "vue";

type Surface = { element: HTMLElement; flatten: () => boolean };
const surfaces = new Set<Surface>();
let observer: MutationObserver | undefined;
let scheduled = false;

function refresh() {
  scheduled = false;
  const ordered = [...surfaces].filter(surface => surface.element.isConnected);
  const registered = new Set(ordered.map(surface => surface.element));
  for (const element of document.querySelectorAll<HTMLElement>(".card, .section-surface, .list-surface, .table-surface, .dashboard-state")) {
    if (!registered.has(element)) ordered.push({ element, flatten: () => false });
  }
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
  if (scheduled) return;
  scheduled = true;
  queueMicrotask(refresh);
}

export function useSurface(root: Ref<HTMLElement | null>, flatten: () => boolean = () => false) {
  let active: Surface | undefined;
  function release() {
    if (active) {
      surfaces.delete(active);
      delete active.element.dataset.nxpSurfaceKind;
      delete active.element.dataset.nxpSurfaceDepth;
      delete active.element.dataset.nxpSurfaceTone;
    }
    active = undefined;
    if (!surfaces.size) { observer?.disconnect(); observer = undefined; }
  }
  function register() {
    release();
    if (!root.value) return;
    active = { element: root.value, flatten };
    surfaces.add(active);
    // Public light-DOM elements have separate Vue apps, so ancestry must follow the rendered tree.
    if (!observer) {
      observer = new MutationObserver(schedule);
      observer.observe(document.documentElement, { childList: true, subtree: true });
    }
    schedule();
  }
  onMounted(register);
  watch(root, register, { flush: "post" });
  watch(flatten, schedule);
  onBeforeUnmount(release);
}
