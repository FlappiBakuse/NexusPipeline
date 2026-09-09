import { $, $$ } from "./dom.js";
import { esc } from "./format.js";
import { icon } from "./icons.js";
import { initAutoScroll, syncAllModeToggles, syncAllSwitchControls } from "./ui.js";
import { focusWithoutTooltip, initTooltips } from "./tooltip.js";
import { applyTranslations, t } from "./i18n.js";

let modalReturnFocus = null;

export function modalShell(title, body, footer = "") {
  const titleId = "modal-title-" + Math.random().toString(36).slice(2);
  return `<div class="modal-header">
    <div><h3 class="modal-title" id="${titleId}">${title}</h3></div>
    <button class="icon-button modal-close" type="button" data-action="close-modal" aria-label="${t("ui.close")}">${icon("close")}</button>
  </div>
  <div class="modal-body">${body}</div>
  ${footer ? `<div class="modal-footer">${footer}</div>` : ""}`;
}

/** 通用确认卡片：确定按钮走 data-action 事件委托（data 透传为 data-* 属性），取消/遮罩/Esc 关闭。</summary> */
export function confirmModal(title, message, confirmAction, data = {}) {
  const dataAttrs = Object.entries(data)
    .map(([key, value]) => ` data-${key}="${esc(value)}"`)
    .join("");
  const isDelete = confirmAction.startsWith("confirm-delete");
  const confirmClass = isDelete || confirmAction === "confirm-cancel-run" ? "danger solid" : "primary";
  const confirmLabel = isDelete
    ? t("ui.confirm_deletion")
    : confirmAction === "restart-confirm" ? t("ui.confirm_restart")
      : confirmAction === "confirm-cancel-run" ? t("ui.confirm_cancellation")
        : t("ui.confirm");
  showModal(modalShell(title, `<p class="modal-copy">${message}</p>`,
    `<button class="ghost" type="button" data-action="close-modal">${t("ui.cancel")}</button><button class="${confirmClass}" type="button" data-action="${esc(confirmAction)}"${dataAttrs}>${confirmLabel}</button>`));
}

export function showModal(content, wide = false, locked = false, allowClose = false) {
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
  modal.className = wide ? "modal wide secondary-surface" : "modal secondary-surface";
  modal.setAttribute("role", "dialog");
  modal.setAttribute("aria-modal", "true");
  if (locked) modal.dataset.locked = "";
  if (allowClose) modal.dataset.allowClose = "";
  modal.innerHTML = content;
  applyTranslations(modal);
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
      focusWithoutTooltip(last);
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      focusWithoutTooltip(first);
    }
  });
  const lockedEscapeHandler = event => {
    if (event.key !== "Escape" || !isLocked() || !mask.isConnected) return;
    event.preventDefault();
    event.stopImmediatePropagation();
  };
  window.addEventListener("keydown", lockedEscapeHandler, true);
  mask._lockedEscapeHandler = lockedEscapeHandler;
  document.body.appendChild(mask);
  // 焦点逃逸兜底——点击弹窗内非焦点区域（activeElement 落 body/外部）后 Tab 不再逃出；
  // 弹窗已移除（关闭流程中）或焦点正常在弹窗内时跳过。
  mask.addEventListener("focusout", event => {
    if (!mask.isConnected) return;
    if (modal.contains(event.relatedTarget)) return;
    const first = $("input, select, textarea", modal) || $("button, a[href]", modal);
    if (first) focusWithoutTooltip(first, { preventScroll: true });
  });
  initAutoScroll(modal);
  syncAllModeToggles(modal);
  syncAllSwitchControls(modal);
  initTooltips();
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
      if (first) focusWithoutTooltip(first, { preventScroll: true });
    }
    // 队列拖拽/新增定时会异步重建整个弹窗；在聚焦与布局完成后恢复 body 滚动，避免回到顶部。
    restorePreviousBodyScroll();
    requestAnimationFrame(restorePreviousBodyScroll);
  });
}

/** 为当前弹窗注册资源清理回调；弹窗重建或关闭时都会执行。 */
export function registerModalCleanup(cleanup) {
  const mask = $(".modal-mask");
  if (!mask || typeof cleanup !== "function") return () => {};
  if (!mask._cleanupCallbacks) mask._cleanupCallbacks = new Set();
  mask._cleanupCallbacks.add(cleanup);
  return () => mask._cleanupCallbacks?.delete(cleanup);
}

export function closeModal(restoreFocus = true) {
  const mask = $(".modal-mask");
  if (mask?._cleanupCallbacks) {
    for (const cleanup of mask._cleanupCallbacks) {
      try {
        cleanup();
      } catch (error) {
        console.error("[NexusPipeline] modal cleanup failed", error);
      }
    }
    mask._cleanupCallbacks.clear();
  }
  if (mask) mask.remove();
  if (mask?._lockedEscapeHandler) window.removeEventListener("keydown", mask._lockedEscapeHandler, true);
  if (restoreFocus && modalReturnFocus && document.contains(modalReturnFocus)) modalReturnFocus.focus();
  modalReturnFocus = null;
}
