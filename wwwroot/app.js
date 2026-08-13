import { initParticles } from "./effects/particles.js";
import { closeModal } from "./core/modal.js";
import { cycleTheme, initTheme, setNavOpen } from "./core/ui.js";
import { enterPage } from "./core/state.js";
import { loadLimits, showWarning, dismissWarningOnce, dismissWarningForever } from "./views/limits.js";
import { pagerNavigate } from "./core/pager.js";
import { actions as dashboardActions, pageDashboard } from "./views/dashboard.js";
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
};

const allActions = {
  ...shellActions,
  ...dashboardActions,
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
  const target = event.target.closest("[data-action]");
  if (!target) return;
  const handler = allActions[target.dataset.action];
  if (handler) handler(target, event);
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
  initParticles();
  await loadLimits();
  showWarning();
  route();
});

/** 远程访问令牌层：API 需要令牌（401 / 探测超时）时显示输入界面；本地访问自动豁免。认证先行，令牌层出现后不再执行后续初始化。 */
async function ensureAccessToken() {
  if (localStorage.getItem("nexus-token")) return true;
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
  if (document.querySelector(".token-mask")) return;
  const mask = document.createElement("div");
  mask.className = "token-mask";
  mask.style.cssText = "position:fixed;inset:0;background:var(--mask,#0008);display:flex;align-items:center;justify-content:center;z-index:1000;";
  mask.innerHTML = `<form class="card token-panel" style="width:min(420px,90vw);padding:24px;display:flex;flex-direction:column;gap:12px;">
    <h3 style="margin:0;">需要访问令牌</h3>
    <p class="muted" style="margin:0;">该 NexusPipeline 已开启远程访问，请输入访问令牌（可在本机「设置 → 远程访问」中查看或重置）。</p>
    <input id="token-input" type="password" autocomplete="off" placeholder="访问令牌" style="padding:10px 12px;border:1px solid var(--border,#d3d1cb);border-radius:8px;background:var(--panel,#fff);color:var(--text,#37352f);">
    <button type="submit" style="padding:10px 14px;border-radius:8px;background:var(--accent,#2eaadc);color:#fff;border:none;">进入管理界面</button>
    <div class="token-error muted" style="color:#d9534f;min-height:1.2em;"></div>
  </form>`;
  document.body.appendChild(mask);
  const input = mask.querySelector("#token-input");
  const errorEl = mask.querySelector(".token-error");
  mask.querySelector("form").addEventListener("submit", async event => {
    event.preventDefault();
    const value = input.value.trim();
    if (!value) {
      errorEl.textContent = "请输入令牌";
      return;
    }
    localStorage.setItem("nexus-token", value);
    const check = await fetch("/api/status", { headers: { Authorization: "Bearer " + value } });
    if (check.ok) {
      location.reload();
    } else {
      localStorage.removeItem("nexus-token");
      errorEl.textContent = "令牌无效，请重试";
    }
  });
  input.focus();
}

window.__showTokenPrompt = showTokenPrompt;
