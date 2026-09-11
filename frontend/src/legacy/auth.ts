import { modalShell, showModal } from "@legacy/core/modal.js";
import { getLocale, t } from "@legacy/core/i18n.js";

declare global {
  interface Window {
    __showTokenPrompt?: () => void;
  }
}

function showTokenPrompt() {
  if (document.querySelector("#token-input")) return;
  showModal(modalShell(
    t("auth.title", {}, "需要访问令牌"),
    `<form id="token-form"><p class="modal-copy">${t("auth.copy", {}, "请输入访问令牌以进入管理界面")}</p><input id="token-input" type="password" autocomplete="off" placeholder="${t("auth.placeholder", {}, "访问令牌")}" aria-label="${t("auth.placeholder", {}, "访问令牌")}"><div id="token-error" class="req" role="alert" aria-live="polite"></div></form>`,
    `<button type="submit" form="token-form">${t("auth.enter", {}, "进入管理界面")}</button>`,
  ), false, true);
  const form = document.querySelector<HTMLFormElement>("#token-form");
  const input = document.querySelector<HTMLInputElement>("#token-input");
  const error = document.querySelector<HTMLElement>("#token-error");
  form?.addEventListener("submit", async event => {
    event.preventDefault();
    const value = input?.value.trim() || "";
    if (!value) {
      if (error) error.textContent = t("auth.required_input", {}, "请输入令牌");
      return;
    }
    try { localStorage.setItem("nexus-token", value); } catch { /* 当前会话继续使用令牌。 */ }
    const response = await fetch("/api/status", { headers: { Authorization: `Bearer ${value}`, "X-Nexus-Locale": getLocale() } });
    if (response.ok) location.reload();
    else if (error) error.textContent = t("auth.invalid", {}, "令牌无效");
  });
}

window.__showTokenPrompt = showTokenPrompt;

export async function ensureAccessToken() {
  const token = (() => { try { return localStorage.getItem("nexus-token"); } catch { return null; } })();
  try {
    const response = await fetch("/api/status", { headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}), "X-Nexus-Locale": getLocale() } });
    if (response.status === 401 || response.headers.get("X-Nexus-Auth") === "required") {
      showTokenPrompt();
      return false;
    }
    return response.ok;
  } catch {
    showTokenPrompt();
    return false;
  }
}
