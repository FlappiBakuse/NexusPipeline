export function orientDropdown(panel: HTMLElement, anchor: DOMRect): void {
  const triggerX = anchor.left + anchor.width / 2, triggerY = anchor.top + anchor.height / 2;
  const left = parseFloat(panel.style.left), top = parseFloat(panel.style.top);
  const x = Math.max(0, Math.min(panel.offsetWidth, triggerX - left));
  const edge = triggerY > top + panel.offsetHeight / 2 ? 'bottom' : 'top';
  panel.style.transformOrigin = `${x}px ${edge}`;
}
