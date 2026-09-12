export type ToastKind = "info" | "error";

let toastTimer: ReturnType<typeof setTimeout> | null = null;
let lastToastMessage: string | null = null;
let lastToastAt = 0;
const SHAKE_WINDOW_MS = 2500;

export function toast(message: string, kind: ToastKind = "info"): void {
  const element = document.getElementById("toast");
  if (!element) return;
  const now = Date.now();
  element.textContent = message;
  element.classList.toggle("error", kind === "error");
  element.classList.remove("shake");
  if (kind === "error" && message === lastToastMessage && now - lastToastAt < SHAKE_WINDOW_MS) {
    void element.offsetWidth;
    element.classList.add("shake");
  }
  if (kind === "error") {
    lastToastMessage = message;
    lastToastAt = now;
  }
  element.classList.remove("hidden");
  if (toastTimer !== null) clearTimeout(toastTimer);
  toastTimer = setTimeout(() => element.classList.add("hidden"), 3200);
}

/** 字段错误：高亮输入框，并把错误写入预留的稳定位置。 */
function visualFieldElement(element: HTMLElement): HTMLElement {
  if (!element?.matches?.("[data-nxp-select-value]")) return element;
  // 下拉控件的可视焦点在触发器上，值载体只是隐藏 input。
  return element.closest<HTMLElement>(".nxp-select")?.querySelector<HTMLElement>(".nxp-select-trigger") || element;
}

function eachFieldElement(element: HTMLElement, callback: (element: HTMLElement) => void): HTMLElement {
  const visual = visualFieldElement(element);
  callback(element);
  if (visual !== element) callback(visual);
  return visual;
}

function markFieldInvalid(id: string, required: boolean): void {
  const element = document.getElementById(id);
  if (!element) return;
  const visual = eachFieldElement(element, item => {
    item.classList.add("field-error");
    item.setAttribute("aria-invalid", "true");
    if (required) item.setAttribute("aria-required", "true");
  });
  const slot = document.getElementById(`${id}-error`);
  if (slot) {
    slot.hidden = true;
    slot.textContent = "";
    const describedBy = (visual.getAttribute("aria-describedby") || "")
      .split(/\s+/)
      .filter(Boolean)
      .filter(value => value !== slot.id);
    if (describedBy.length) visual.setAttribute("aria-describedby", describedBy.join(" "));
    else visual.removeAttribute("aria-describedby");
  }
  visual.focus({ preventScroll: true });
}

/** 必填空值错误：保留红色边框与可访问性状态，不在字段下方显示红色文案。 */
export function setRequiredFieldError(id: string): void {
  markFieldInvalid(id, true);
}

/** 清除字段内联错误（无错误时无操作）。 */
export function clearFieldError(id: string): void {
  const element = document.getElementById(id);
  if (!element) return;
  const visual = eachFieldElement(element, item => {
    item.classList.remove("field-error");
    item.removeAttribute("aria-invalid");
  });
  const slot = document.getElementById(`${id}-error`);
  if (slot) {
    slot.hidden = true;
    slot.textContent = "";
    const describedBy = (visual.getAttribute("aria-describedby") || "").split(/\s+/).filter(Boolean).filter(value => value !== slot.id);
    if (describedBy.length) visual.setAttribute("aria-describedby", describedBy.join(" "));
    else visual.removeAttribute("aria-describedby");
  }
}
