let tooltip: HTMLElement | null = null;
let target: HTMLElement | null = null;
let anchor: HTMLElement | null = null;
let timer: number | null = null;
let bound = false;
let sequence = 0;

interface TooltipContext {
  target: HTMLElement;
  anchor: HTMLElement;
  trigger?: "pointer" | "focus";
}

function helpText(element: HTMLElement | null): string {
  return String(element?.dataset?.help || element?.dataset?.tooltip || "").trim();
}

function clearTimer(): void {
  if (timer !== null) {
    window.clearTimeout(timer);
    timer = null;
  }
}

function removeTargetDescription(): void {
  const describedTarget = anchor || target;
  if (!describedTarget || !tooltip) return;
  const describedBy = (describedTarget.getAttribute("aria-describedby") || "")
    .split(/\s+/)
    .filter(Boolean)
    .filter(value => value !== tooltip!.id);
  if (describedBy.length) describedTarget.setAttribute("aria-describedby", describedBy.join(" "));
  else describedTarget.removeAttribute("aria-describedby");
}

export function hideTooltip(): void {
  clearTimer();
  removeTargetDescription();
  tooltip?.remove();
  tooltip = null;
  target = null;
  anchor = null;
}

function positionTooltip(): void {
  if (!tooltip || !anchor?.isConnected) return;
  const margin = 12;
  const gap = 11;
  const rect = anchor.getBoundingClientRect();
  const tooltipRect = tooltip.getBoundingClientRect();
  const viewportWidth = window.innerWidth || document.documentElement?.clientWidth || 0;
  const viewportHeight = window.innerHeight || document.documentElement?.clientHeight || 0;
  const topSpace = rect.top - gap - margin;
  const bottomSpace = viewportHeight - rect.bottom - gap - margin;
  const placement = topSpace >= tooltipRect.height || topSpace >= bottomSpace ? "top" : "bottom";
  let top = placement === "top"
    ? rect.top - tooltipRect.height - gap
    : rect.bottom + gap;
  top = Math.min(Math.max(margin, top), Math.max(margin, viewportHeight - tooltipRect.height - margin));
  const anchorCenter = rect.left + rect.width / 2;
  const minLeft = margin + tooltipRect.width / 2;
  const maxLeft = viewportWidth - margin - tooltipRect.width / 2;
  const left = maxLeft >= minLeft
    ? Math.min(Math.max(minLeft, anchorCenter), maxLeft)
    : viewportWidth / 2;
  tooltip.dataset.placement = placement;
  tooltip.style.left = `${Math.round(left)}px`;
  tooltip.style.top = `${Math.round(top)}px`;
}

function showNow(context: TooltipContext): void {
  const element = context?.target;
  const text = helpText(element);
  if (!text || !isContextActive(context)) return;
  hideTooltip();
  target = element;
  anchor = context.anchor || element;
  tooltip = document.createElement("div");
  tooltip.id = `nxp-tooltip-${++sequence}`;
  tooltip.className = "nxp-tooltip secondary-surface";
  tooltip.setAttribute("role", "tooltip");
  tooltip.textContent = text;
  document.body.append(tooltip);
  const describedTarget = anchor || element;
  const describedBy = (describedTarget.getAttribute("aria-describedby") || "").split(/\s+/).filter(Boolean);
  describedBy.push(tooltip.id);
  describedTarget.setAttribute("aria-describedby", [...new Set(describedBy)].join(" "));
  positionTooltip();
}

function schedule(context: TooltipContext): void {
  clearTimer();
  const element = context?.target;
  if (!helpText(element)) return;
  timer = window.setTimeout(() => {
    timer = null;
    if (!isContextActive(context)) return;
    showNow(context);
  }, 680);
}

function isContextActive(context: TooltipContext): boolean {
  const element = context?.target;
  const contextAnchor = context?.anchor;
  if (!element?.isConnected || !contextAnchor?.isConnected || !helpText(element)) return false;
  if (context.trigger === "focus") return document.activeElement === contextAnchor;
  if (context.trigger === "pointer") return element.matches(":hover");
  return true;
}

function closestHelpTarget(node: EventTarget | null): HTMLElement | null {
  const element = node instanceof Element ? node : null;
  return element?.closest<HTMLElement>("[data-help], [data-tooltip]") || null;
}

function closestHelpCont(node: EventTarget | null): TooltipContext | null {
  const element = node instanceof Element ? node : null;
  const helpTarget = closestHelpTarget(element);
  if (!helpTarget) return null;
  const nonHelpControl = element?.closest("[data-path-trigger], [data-nxp-step]");
  if (nonHelpControl && helpTarget.contains(nonHelpControl)) return null;
  const directAnchor = element?.closest<HTMLElement>("input:not([type=hidden]), textarea, button, select, [contenteditable=\"true\"], [tabindex]:not([tabindex=\"-1\"])");
  const fallbackAnchor = helpTarget.querySelector<HTMLElement>("input:not([type=hidden]), textarea, button, select, [contenteditable=\"true\"], [tabindex]:not([tabindex=\"-1\"])");
  return {
    target: helpTarget,
    anchor: directAnchor && helpTarget.contains(directAnchor) ? directAnchor : fallbackAnchor || helpTarget,
  };
}

export function initTooltips(): void {
  if (bound || typeof document === "undefined") return;
  bound = true;
  document.addEventListener("pointerover", event => {
    const next = closestHelpTarget(event.target);
    const related = closestHelpTarget(event.relatedTarget);
    if (!next) return;
    const context = closestHelpCont(event.target);
    if (!context) {
      hideTooltip();
      return;
    }
    if (next === related && closestHelpCont(event.relatedTarget)?.anchor === context.anchor) return;
    schedule({ ...context, trigger: "pointer" });
  });
  document.addEventListener("pointerout", event => {
    const current = closestHelpTarget(event.target);
    const related = closestHelpTarget(event.relatedTarget);
    if (current && current !== related) hideTooltip();
  });
  document.addEventListener("focusin", event => {
    const context = closestHelpCont(event.target);
    if (!context) {
      hideTooltip();
      return;
    }
    const focusContext: TooltipContext = { ...context, trigger: "focus" };
    const scheduleAfterFocusScroll = () => {
      if (document.activeElement === focusContext.anchor && focusContext.anchor?.isConnected) schedule(focusContext);
    };
    if (typeof window.requestAnimationFrame === "function") window.requestAnimationFrame(scheduleAfterFocusScroll);
    else window.setTimeout(scheduleAfterFocusScroll, 0);
  });
  document.addEventListener("focusout", event => {
    const current = closestHelpTarget(event.target);
    if (current && !current.contains(event.relatedTarget as Node | null)) hideTooltip();
  });
  document.addEventListener("pointerdown", event => {
    if (tooltip && !tooltip.contains(event.target as Node) && !target?.contains(event.target as Node)) hideTooltip();
  });
  document.addEventListener("keydown", event => {
    if (event.key === "Escape") hideTooltip();
  });
  document.addEventListener("scroll", hideTooltip, true);
  window.addEventListener("resize", hideTooltip);
  window.addEventListener("blur", hideTooltip);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) hideTooltip();
  });
}
