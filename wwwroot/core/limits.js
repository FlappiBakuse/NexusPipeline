import { api } from "../core/api.js";
import { esc } from "../core/format.js";
import { state } from "../core/state.js";
import { t } from "./i18n.js";

const DISMISS_KEY = "nexus-limits-dismissed";

export async function loadLimits() {
  try {
    const data = await api("GET", "/api/limits");
    state.limits = data.limits;
    state.limitsWarnings = data.warnings || [];
    return data;
  } catch {
    return null;
  }
}

function warningHash() {
  return (state.limitsWarnings || []).join("|");
}

export function shouldShowWarning() {
  const hash = warningHash();
  if (!hash) return false;
  try {
    return localStorage.getItem(DISMISS_KEY) !== hash;
  } catch {
    return true;
  }
}

let warningReturnFocus = null;

/** 约束警告层（ 补齐无障碍）：role=alertdialog + aria-labelledby + 初始焦点 + 焦点陷阱 + Esc + 焦点恢复。 */
export function showWarning() {
  if (!shouldShowWarning()) return;
  if (document.getElementById("limits-warning")) return;
  warningReturnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  const mask = document.createElement("div");
  mask.id = "limits-warning";
  mask.className = "limits-warning";
  mask.setAttribute("role", "alertdialog");
  mask.setAttribute("aria-modal", "true");
  mask.setAttribute("aria-labelledby", "limits-warning-title");
  const items = (state.limitsWarnings || []).map(item => `<li>${esc(item)}</li>`).join("");
  mask.innerHTML = `<div class="limits-warning-card">
    <h3 class="modal-title" id="limits-warning-title">${t("common.limits_warning_title")}</h3>
    <p class="limits-warning-copy">${t("common.limits_warning_copy")}</p>
    <ul class="limits-warning-list">${items}</ul>
    <div class="modal-footer"><button type="button" data-action="limits-dismiss-once">${t("common.acknowledge")}</button><button class="ghost" type="button" data-action="limits-dismiss-forever">${t("common.do_not_remind_again")}</button></div>
  </div>`;
  document.body.appendChild(mask);
  // 焦点陷阱（Tab/Shift+Tab 限制在警告层内）与 Esc 关闭。
  mask.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      event.preventDefault();
      hideWarning();
      return;
    }
    if (event.key !== "Tab") return;
    const focusable = Array.from(mask.querySelectorAll('button, input, select, textarea, a[href], [tabindex]:not([tabindex="-1"])'))
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
  const firstButton = mask.querySelector("button");
  if (firstButton) firstButton.focus();
}

export function hideWarning() {
  const mask = document.getElementById("limits-warning");
  if (!mask) return;
  mask.remove();
  if (warningReturnFocus && document.contains(warningReturnFocus)) warningReturnFocus.focus();
  warningReturnFocus = null;
}

export function dismissWarningOnce() {
  hideWarning();
}

export function dismissWarningForever() {
  try {
    localStorage.setItem(DISMISS_KEY, warningHash());
  } catch {
  }
  hideWarning();
}
