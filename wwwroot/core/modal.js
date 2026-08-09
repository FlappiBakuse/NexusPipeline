import { $, $$ } from "./dom.js";
import { initAutoScroll } from "./ui.js";

let modalReturnFocus = null;

export function modalShell(title, body, footer = "") {
  const titleId = "modal-title-" + Math.random().toString(36).slice(2);
  return `<div class="modal-header">
    <div><h3 class="modal-title" id="${titleId}">${title}</h3></div>
    <button class="icon-button modal-close" type="button" data-action="close-modal" aria-label="关闭">×</button>
  </div>
  <div class="modal-body">${body}</div>
  ${footer ? `<div class="modal-footer">${footer}</div>` : ""}`;
}

export function showModal(content, wide = false) {
  closeModal();
  modalReturnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  const mask = document.createElement("div");
  mask.className = "modal-mask";
  mask.setAttribute("role", "presentation");
  const modal = document.createElement("div");
  modal.className = wide ? "modal wide" : "modal";
  modal.setAttribute("role", "dialog");
  modal.setAttribute("aria-modal", "true");
  modal.innerHTML = content;
  const heading = $("[id^='modal-title-']", modal);
  if (heading) modal.setAttribute("aria-labelledby", heading.id);
  mask.appendChild(modal);
  mask.addEventListener("mousedown", event => {
    if (event.target === mask) closeModal();
  });
  mask.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      event.preventDefault();
      closeModal();
      return;
    }
    if (event.key !== "Tab") return;
    const focusable = $$('button, input, select, textarea, a[href], [tabindex]:not([tabindex="-1"])', modal)
      .filter(element => !element.disabled && element.offsetParent !== null);
    if (!focusable.length) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  });
  document.body.appendChild(mask);
  initAutoScroll(modal);
  requestAnimationFrame(() => {
    if (modal.contains(document.activeElement)) return;
    const first = $("input, select, textarea", modal) || $("button, a[href]", modal);
    if (first) first.focus();
  });
}

export function closeModal() {
  const mask = $(".modal-mask");
  if (mask) mask.remove();
  if (modalReturnFocus && document.contains(modalReturnFocus)) modalReturnFocus.focus();
  modalReturnFocus = null;
}
