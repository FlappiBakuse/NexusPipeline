export interface FloatingPositionOptions {
  gap?: number;
  margin?: number;
  minWidth?: number;
}

export function positionFloatingOverlay(
  anchor: HTMLElement,
  overlay: HTMLElement,
  options: FloatingPositionOptions = {},
) {
  const margin = options.margin ?? 8;
  const gap = options.gap ?? 6;
  const anchorRect = anchor.getBoundingClientRect();
  if (!anchorRect.width || !anchorRect.height) return null;

  const viewportWidth = Math.max(0, window.innerWidth);
  const viewportHeight = Math.max(0, window.innerHeight);
  const availableWidth = Math.max(0, viewportWidth - margin * 2);
  const width = Math.min(
    Math.max(anchorRect.width, options.minWidth ?? anchorRect.width),
    availableWidth,
  );
  const left = Math.min(
    Math.max(margin, anchorRect.left),
    Math.max(margin, viewportWidth - width - margin),
  );

  overlay.style.position = "fixed";
  overlay.style.left = `${Math.round(left)}px`;
  overlay.style.right = "auto";
  overlay.style.width = `${Math.round(width)}px`;
  overlay.style.maxWidth = `calc(100vw - ${margin * 2}px)`;

  const naturalHeight = Math.max(0, overlay.offsetHeight);
  const below = Math.max(0, viewportHeight - anchorRect.bottom - gap - margin);
  const above = Math.max(0, anchorRect.top - gap - margin);
  const opensAbove = naturalHeight > below && above > below;
  const availableHeight = Math.max(0, opensAbove ? above : below);
  const top = opensAbove
    ? Math.max(margin, anchorRect.top - gap - Math.min(naturalHeight, availableHeight))
    : Math.min(viewportHeight - margin, anchorRect.bottom + gap);

  overlay.style.top = `${Math.round(top)}px`;
  overlay.style.bottom = "auto";
  overlay.style.maxHeight = `${Math.max(80, Math.round(availableHeight))}px`;
  return { top, left, width, maxHeight: Math.max(80, availableHeight) };
}

export function bindFloatingReposition(reposition: () => void) {
  document.addEventListener("scroll", reposition, true);
  window.addEventListener("resize", reposition);
  return () => {
    document.removeEventListener("scroll", reposition, true);
    window.removeEventListener("resize", reposition);
  };
}
