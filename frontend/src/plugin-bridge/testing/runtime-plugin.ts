export const cardCleanup: boolean[] = [];
export function activate(host: any) {
  const card = host.dashboard.registerCard("carousel", (surface: any) => {
    surface.element.textContent = "mounted card";
    return () => { cardCleanup.push(surface.signal.aborted); };
  });
  const slot = host.slots.register("dispatch.running.sidecar", (surface: any) => {
    surface.element.textContent = "mounted sidecar";
    return () => { surface.element.textContent = "disposed sidecar"; };
  });
  return () => { card.dispose(); slot.dispose(); };
}
