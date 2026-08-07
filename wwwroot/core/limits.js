import { api } from "./api.js";
import { esc } from "./format.js";
import { state } from "./state.js";

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

export function showWarning() {
  if (!shouldShowWarning()) return;
  if (document.getElementById("limits-warning")) return;
  const mask = document.createElement("div");
  mask.id = "limits-warning";
  mask.className = "limits-warning";
  mask.setAttribute("role", "alertdialog");
  mask.setAttribute("aria-modal", "true");
  const items = (state.limitsWarnings || []).map(item => `<li>${esc(item)}</li>`).join("");
  mask.innerHTML = `<div class="limits-warning-card">
    <h3 class="modal-title">约束配置警告</h3>
    <p class="limits-warning-copy">config/limits.json 中的约束配置超出绝对安全区间，程序已按配置生效，请注意数据规模。</p>
    <ul class="limits-warning-list">${items}</ul>
    <div class="modal-footer"><button type="button" data-action="limits-dismiss-once">知道了</button><button class="ghost" type="button" data-action="limits-dismiss-forever">不再提醒</button></div>
  </div>`;
  document.body.appendChild(mask);
}

export function hideWarning() {
  document.getElementById("limits-warning")?.remove();
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
