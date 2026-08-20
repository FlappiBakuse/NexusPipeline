import { $, $$ } from "./dom.js";
import { esc } from "./format.js";
import { icon } from "./icons.js";
import { initAutoScroll, syncAllModeToggles } from "./ui.js";

let modalReturnFocus = null;

export function modalShell(title, body, footer = "") {
  const titleId = "modal-title-" + Math.random().toString(36).slice(2);
  return `<div class="modal-header">
    <div><h3 class="modal-title" id="${titleId}">${title}</h3></div>
    <button class="icon-button modal-close" type="button" data-action="close-modal" aria-label="关闭">${icon("close")}</button>
  </div>
  <div class="modal-body">${body}</div>
  ${footer ? `<div class="modal-footer">${footer}</div>` : ""}`;
}

/** 通用确认卡片：确定按钮走 data-action 事件委托（data 透传为 data-* 属性），取消/遮罩/Esc 关闭。</summary> */
export function confirmModal(title, message, confirmAction, data = {}) {
  const dataAttrs = Object.entries(data)
    .map(([key, value]) => ` data-${key}="${esc(value)}"`)
    .join("");
  const confirmClass = confirmAction.startsWith("confirm-delete") ? "danger" : "";
  showModal(modalShell(title, `<p class="modal-copy">${message}</p>`,
    `<button class="${confirmClass}" type="button" data-action="${esc(confirmAction)}"${dataAttrs}>确定</button><button class="ghost" type="button" data-action="close-modal">取消</button>`));
}

export function showModal(content, wide = false, locked = false) {
  const previousBody = $(".modal-mask .modal-body");
  const previousScroll = previousBody
    ? { left: previousBody.scrollLeft, top: previousBody.scrollTop }
    : null;
  const previousReturnFocus = modalReturnFocus;
  // 内部重建弹窗时不要先把焦点还给底层页面按钮，否则底层窗口可能被滚回按钮所在位置。
  closeModal(false);
  modalReturnFocus = previousReturnFocus || (document.activeElement instanceof HTMLElement ? document.activeElement : null);
  const mask = document.createElement("div");
  mask.className = "modal-mask";
  mask.setAttribute("role", "presentation");
  const modal = document.createElement("div");
  modal.className = wide ? "modal wide" : "modal";
  modal.setAttribute("role", "dialog");
  modal.setAttribute("aria-modal", "true");
  if (locked) modal.dataset.locked = "";
  modal.innerHTML = content;
  const heading = $("[id^='modal-title-']", modal);
  if (heading) modal.setAttribute("aria-labelledby", heading.id);
  mask.appendChild(modal);
  const isLocked = () => modal.dataset.locked !== undefined;
  mask.addEventListener("mousedown", event => {
    if (event.target === mask && !isLocked()) closeModal();
  });
  mask.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      event.preventDefault();
      if (!isLocked()) closeModal();
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
  // v0.7.3+（KN-12）：焦点逃逸兜底——点击弹窗内非焦点区域（activeElement 落 body/外部）后 Tab 不再逃出；
  // 弹窗已移除（关闭流程中）或焦点正常在弹窗内时跳过。
  mask.addEventListener("focusout", event => {
    if (!mask.isConnected) return;
    if (modal.contains(event.relatedTarget)) return;
    const first = $("input, select, textarea", modal) || $("button, a[href]", modal);
    if (first) first.focus();
  });
  initAutoScroll(modal);
  syncAllModeToggles(modal);
  const restorePreviousBodyScroll = () => {
    if (!previousScroll) return;
    const nextBody = $(".modal-mask .modal-body");
    if (!nextBody) return;
    nextBody.scrollLeft = previousScroll.left;
    nextBody.scrollTop = previousScroll.top;
  };
  requestAnimationFrame(() => {
    if (!modal.contains(document.activeElement)) {
      const first = $("input, select, textarea", modal) || $("button, a[href]", modal);
      if (first) first.focus({ preventScroll: true });
    }
    // 队列拖拽/新增定时会异步重建整个弹窗；在聚焦与布局完成后恢复 body 滚动，避免回到顶部。
    restorePreviousBodyScroll();
    requestAnimationFrame(restorePreviousBodyScroll);
  });
}

export function closeModal(restoreFocus = true) {
  const mask = $(".modal-mask");
  if (mask) mask.remove();
  if (restoreFocus && modalReturnFocus && document.contains(modalReturnFocus)) modalReturnFocus.focus();
  modalReturnFocus = null;
}
