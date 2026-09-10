import { closeModal, showModal, modalShell } from "@legacy/core/modal.js";
import { cancelSystemAction, closeMoreMenus, cycleTheme, setNavOpen, syncModeToggleText, syncSwitchControl, toggleMoreMenu } from "@legacy/core/ui.js";
import { dismissWarningForever, dismissWarningOnce } from "@legacy/core/limits.js";
import { pagerNavigate } from "@legacy/core/pager.js";
import { pickPath } from "@legacy/core/path-picker.js";
import { actions as scriptsActions, syncScriptGhostState } from "@legacy/views/scripts.js";
import { actions as usersActions } from "@legacy/views/users/index.js";
import { actions as queuesActions } from "@legacy/views/queues.js";
import { actions as dispatchActions } from "@legacy/views/dispatch.js";
import { actions as historyActions } from "@legacy/views/history.js";
import { actions as pluginsActions } from "@legacy/views/plugins.js";
import { actions as settingsActions } from "@legacy/views/settings.js";
import { getLocale, t } from "@legacy/core/i18n.js";

const actions: Record<string, (target: Element, event?: Event) => void> = {
  "open-nav": () => setNavOpen(true),
  "close-nav": () => setNavOpen(false),
  "toggle-theme": () => cycleTheme(),
  "close-modal": () => closeModal(),
  "limits-dismiss-once": () => dismissWarningOnce(),
  "limits-dismiss-forever": () => dismissWarningForever(),
  "pager-page": target => pagerNavigate((target as HTMLElement).dataset.pager, "page", target),
  "pager-prev": target => pagerNavigate((target as HTMLElement).dataset.pager, "prev", target),
  "pager-next": target => pagerNavigate((target as HTMLElement).dataset.pager, "next", target),
  "cancel-system-action": () => cancelSystemAction(),
  "toggle-more-menu": target => toggleMoreMenu(target),
  "pick-path": target => pickPath(target),
  ...scriptsActions,
  ...usersActions,
  ...queuesActions,
  ...dispatchActions,
  ...historyActions,
  ...pluginsActions,
  ...settingsActions,
};

let installed = false;

function resolveAction(name: string) {
  return actions[name];
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

export function installLegacyEventBridge() {
  if (installed) return;
  installed = true;
  document.addEventListener("click", event => {
    const target = event.target instanceof Element ? event.target : null;
    if (!target) return;
    if (target.closest(".nav-backdrop")) {
      setNavOpen(false);
      return;
    }
    if (!target.closest(".overflow-menu-wrap")) closeMoreMenus();
    const actionTarget = target.closest("[data-action]");
    if (actionTarget) resolveAction(actionTarget.getAttribute("data-action") || "")?.(actionTarget, event);
    if (actionTarget?.matches('[role="menuitem"]')) closeMoreMenus();
    const toggle = target.closest(".mode-toggle");
    if (toggle) syncModeToggleText(toggle);
    if (toggle) syncSwitchControl(toggle);
  });
  document.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      if (document.querySelector(".modal[data-locked]")) return;
      const menu = document.querySelector<HTMLElement>(".overflow-menu:not([hidden])");
      if (menu) {
        event.preventDefault();
        closeMoreMenus({ restoreFocus: true });
      }
      return;
    }
    if (event.key !== "Enter" && event.key !== " ") return;
    const target = event.target instanceof Element ? event.target.closest('[data-action][role="button"]') : null;
    if (!target || target.matches("button, a, input, select, textarea")) return;
    const handler = resolveAction(target.getAttribute("data-action") || "");
    if (!handler) return;
    event.preventDefault();
    handler(target, event);
  }, true);
  document.addEventListener("input", event => {
    const target = event.target instanceof Element ? event.target : null;
    if (!target) return;
    if ((target as HTMLInputElement).id === "sm-root") syncScriptGhostState();
    const actionTarget = target.closest('[data-action="sync-user-management-run-days"], [data-action="filter-plugin-list"]');
    if (actionTarget) resolveAction(actionTarget.getAttribute("data-action") || "")?.(actionTarget, event);
  });
  document.addEventListener("change", event => {
    const target = event.target instanceof Element ? event.target.closest("[data-action]") : null;
    if (target) resolveAction(target.getAttribute("data-action") || "")?.(target, event);
  });
}
