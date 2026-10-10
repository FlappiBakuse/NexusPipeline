import type { DashboardCardRenderer } from "./types";

type Registration = { renderer: DashboardCardRenderer; mounts: Set<Mount> };
type Mount = { controller: AbortController; cleanup?: () => void; disposed: boolean; element: HTMLElement; surface: HTMLElement; registration: Registration };
const renderers = new Map<string, Registration>();
const mounts = new WeakMap<HTMLElement, Mount>();
const listeners = new Set<() => void>();
function changed() { for (const listener of listeners) { try { listener(); } catch (error) { console.warn("Dashboard listener failed", error); } } }
export function disposeDashboardOwner(owner: string) {
  for (const [id, registration] of [...renderers]) if (id.startsWith(`plugin:${owner}:`)) {
    renderers.delete(id);
    for (const mount of [...registration.mounts]) dispose(mount);
  }
  changed();
}
export function onDashboardRenderersChanged(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; }
export function hasDashboardRenderer(cardId: string): boolean { return renderers.has(cardId); }
function dispose(mount: Mount) {
  if (mount.disposed) return;
  mount.disposed = true;
  mount.controller.abort();
  mount.registration.mounts.delete(mount);
  const cleanup = mount.cleanup;
  mount.cleanup = undefined;
  try { cleanup?.(); } catch (error) { console.warn("Dashboard cleanup failed", error); } finally {
    mount.surface.remove();
    if (mounts.get(mount.element) === mount) mounts.delete(mount.element);
  }
}
export function disposeDashboardCard(element: HTMLElement) { const mount = mounts.get(element); if (mount) dispose(mount); }
export function registerDashboardCard(owner: string, localId: string, renderer: DashboardCardRenderer) {
  if (!/^[a-z0-9][a-z0-9-]{0,63}$/.test(localId) || typeof renderer !== "function") throw new Error("dashboard_card_invalid");
  const id = `plugin:${owner}:${localId}`;
  if (renderers.has(id)) throw new Error("dashboard_card_duplicate");
  const registration: Registration = { renderer, mounts: new Set() };
  renderers.set(id, registration);
  changed();
  return { dispose() {
    if (renderers.get(id) !== registration) return;
    renderers.delete(id);
    for (const mount of [...registration.mounts]) dispose(mount);
    changed();
  } };
}
export async function renderDashboardCard(element: HTMLElement, cardId: string) {
  const registration = renderers.get(cardId);
  const existing = mounts.get(element);
  if (existing && existing.registration === registration && !existing.disposed) return;
  disposeDashboardCard(element);
  element.replaceChildren();
  if (!registration) return;
  // Each registration owns a separate surface, so a late writer cannot touch its replacement.
  const surface = document.createElement("div");
  element.append(surface);
  const mount: Mount = { controller: new AbortController(), disposed: false, element, surface, registration };
  mounts.set(element, mount);
  registration.mounts.add(mount);
  try {
    const cleanup = await registration.renderer({ element: surface, cardId, signal: mount.controller.signal, context: Object.freeze({}) });
    if (typeof cleanup === "function") {
      if (mount.disposed) cleanup(); else mount.cleanup = cleanup;
    }
  } catch (error) {
    if (mount.disposed) return;
    dispose(mount);
    showError(element, cardId);
    console.warn("Dashboard renderer failed", cardId, error);
  }
}
function showError(element: HTMLElement, cardId: string) {
  const message = document.createElement("p");
  message.textContent = `${cardId}: renderer unavailable`;
  message.setAttribute("role", "status");
  const retry = document.createElement("nxp-button");
  retry.textContent = "Retry / 重试";
  retry.addEventListener("click", () => void renderDashboardCard(element, cardId));
  element.replaceChildren(message, retry);
}
