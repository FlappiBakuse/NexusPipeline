import { initParticles } from "./effects/particles.js";
import { closeModal, showModal, modalShell } from "./core/modal.js";
import { cancelSystemAction, closeMoreMenus, cycleTheme, initTheme, setNavOpen, syncModeToggleText, syncSwitchControl, toggleMoreMenu } from "./core/ui.js";
import { enterPage } from "./core/state.js";
import { loadLimits, showWarning, dismissWarningOnce, dismissWarningForever } from "./views/limits.js";
import { pagerNavigate } from "./core/pager.js";
import { pageDashboard } from "./views/dashboard.js";
import { actions as scriptsActions, pageScripts, syncScriptGhostState } from "./views/scripts.js";
import { actions as usersActions, pageScriptUsers } from "./views/users.js";
import { actions as queuesActions, pageQueues } from "./views/queues.js";
import { actions as dispatchActions, pageDispatch } from "./views/dispatch.js";
import { actions as historyActions, pageHistory } from "./views/history.js";
import { actions as pluginsActions, pagePlugins, pagePluginConfig } from "./views/plugins.js";
import { actions as settingsActions, pageSettings } from "./views/settings.js";

const shellActions = {
  "open-nav": () => setNavOpen(true),
  "close-nav": () => setNavOpen(false),
  "toggle-theme": () => cycleTheme(),
  "close-modal": () => closeModal(),
  "limits-dismiss-once": () => dismissWarningOnce(),
  "limits-dismiss-forever": () => dismissWarningForever(),
  "pager-page": target => pagerNavigate(target.dataset.pager, "page", target),
  "pager-prev": target => pagerNavigate(target.dataset.pager, "prev", target),
  "pager-next": target => pagerNavigate(target.dataset.pager, "next", target),
  "cancel-system-action": () => cancelSystemAction(),
  "toggle-more-menu": target => toggleMoreMenu(target),
};

const allActions = {
  ...shellActions,
  ...scriptsActions,
  ...usersActions,
  ...queuesActions,
  ...dispatchActions,
  ...historyActions,
  ...pluginsActions,
  ...settingsActions,
};

const routes = { dashboard: pageDashboard, scripts: pageScripts, queues: pageQueues, dispatch: pageDispatch, history: pageHistory, plugins: pagePlugins, settings: pageSettings };

function route() {
  closeModal();
  setNavOpen(false);
  const hash = (location.hash || "#/dashboard").slice(2) || "dashboard";
  const token = enterPage(hash);
  const segments = hash.split("/");
  if (segments[0] === "scripts" && segments[1] && segments[2] === "users") {
    pageScriptUsers(segments[1], token);
    return;
  }
  if (segments[0] === "plugins" && segments[1]) {
    pagePluginConfig(segments[1], token);
    return;
  }
  (routes[segments[0]] || pageDashboard)(token);
}

document.addEventListener("click", event => {
  // v0.7.3+（P1-3）：移动端导航遮罩（含铺满遮罩的关闭按钮）点击即关闭。
  if (event.target.closest(".nav-backdrop")) {
    setNavOpen(false);
    return;
  }
  if (!event.target.closest(".overflow-menu-wrap")) closeMoreMenus();
  const target = event.target.closest("[data-action]");
  if (!target) return;
  // v0.7.4（KN-44）：原生 select 的 data-action 由 change 事件唯一分发——select 上点击（打开/选项变更）同样会
  // 触发本 click 委托，与 change 委托叠加即双触发（当前调用点幂等未暴露，属隐患模式）。
  if (target.matches("select") || target.matches("option")) return;
  const handler = allActions[target.dataset.action];
  if (handler) handler(target, event);
  if (target.matches('[role="menuitem"]')) closeMoreMenus();
  const toggleBtn = event.target.closest(".mode-toggle");
  if (toggleBtn) syncModeToggleText(toggleBtn);
  if (toggleBtn) syncSwitchControl(toggleBtn);
});

document.addEventListener("keydown", event => {
  if (event.key === "Escape") {
    const openMenu = document.querySelector(".overflow-menu:not([hidden])");
    if (openMenu) {
      event.preventDefault();
      closeMoreMenus({ restoreFocus: true });
    }
    return;
  }
  if (event.key !== "Enter" && event.key !== " ") return;
  const target = event.target.closest('[data-action][role="button"]');
  if (!target || target.matches("button, a, input, select, textarea")) return;
  const handler = allActions[target.dataset.action];
  if (!handler) return;
  event.preventDefault();
  handler(target, event);
});

document.addEventListener("input", event => {
  if (event.target?.id === "sm-root") syncScriptGhostState();
});

document.addEventListener("change", event => {
  const target = event.target.closest("[data-action]");
  if (!target) return;
  const handler = allActions[target.dataset.action];
  if (handler) handler(target, event);
});

window.addEventListener("hashchange", route);
window.addEventListener("resize", () => {
  if (window.innerWidth > 820) setNavOpen(false);
});
window.addEventListener("DOMContentLoaded", async () => {
  initTheme();
  if (!(await ensureAccessToken())) return;
  updateLocalAddr();
  initParticles();
  await loadLimits();
  showWarning();
  route();
});

/** v0.7.4（KN-47）：侧栏服务地址按实际监听端口/访问主机显示（此前硬编码 127.0.0.1，端口漂移或远程访问时不准确）。 */
async function updateLocalAddr() {
  const el = document.getElementById("local-addr");
  if (!el) return;
  try {
    const res = await fetch("/api/status", { cache: "no-store" });
    const data = await res.json();
    const port = data.actualPort || data.webPort || "";
    el.textContent = port ? `服务 · ${location.hostname}:${port}` : "服务";
  } catch {
    el.textContent = "服务";
  }
}

/** 远程访问令牌层：API 需要令牌（401 / 探测超时）时显示输入界面；本地访问自动豁免。认证先行，令牌层出现后不再执行后续初始化。 */
async function ensureAccessToken() {
  // v0.7.4（KN-45）：存储不可用（隐私模式/禁用存储）时按「无已存令牌」处理，避免 getItem 抛异常中断初始化白屏。
  let storedToken = null;
  try {
    storedToken = localStorage.getItem("nexus-token");
  } catch {
    storedToken = null;
  }
  if (storedToken) return true;
  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 10000);
    let res;
    try {
      res = await fetch("/api/status", { signal: controller.signal });
    } finally {
      clearTimeout(timer);
    }
    if (res && (res.status === 401 || res.headers.get("X-Nexus-Auth") === "required")) {
      showTokenPrompt();
      return false;
    }
    if (!res) {
      showTokenPrompt();
      return false;
    }
    return true;
  } catch {
    showTokenPrompt();
    return false;
  }
}

function showTokenPrompt() {
  if (document.querySelector("#token-input")) return;
  // v0.6.9+（P12）：复用 modal 组件（role=dialog/aria-modal/aria-labelledby/焦点陷阱，locked 锁定不可 Esc/遮罩关闭），
  // 移除内联 style 与硬编码色值（此前 token-mask 自绘遮罩违反前端自约束）。
  showModal(modalShell("需要访问令牌",
    `<form id="token-form"><p class="modal-copy">该 NexusPipeline 已开启远程访问，请输入访问令牌（可在本机「设置 → 远程访问」中查看或重置）。</p><input id="token-input" type="password" autocomplete="off" placeholder="访问令牌" aria-label="访问令牌"><div id="token-error" class="req" role="alert" aria-live="polite"></div></form>`,
    `<button type="submit" form="token-form">进入管理界面</button>`), false, true);
  const form = document.querySelector("#token-form");
  const input = document.querySelector("#token-input");
  const errorEl = document.querySelector("#token-error");
  form.addEventListener("submit", async event => {
    event.preventDefault();
    const value = input.value.trim();
    if (!value) {
      errorEl.textContent = "请输入令牌";
      return;
    }
    try {
      localStorage.setItem("nexus-token", value);
    } catch {
      // 存储不可用：令牌仅本次请求使用，刷新后需重输。
    }
    const check = await fetch("/api/status", { headers: { Authorization: "Bearer " + value } });
    if (check.ok) {
      location.reload();
    } else {
      try {
        localStorage.removeItem("nexus-token");
      } catch {
      }
      errorEl.textContent = "令牌无效，请重试";
    }
  });
}

window.__showTokenPrompt = showTokenPrompt;
