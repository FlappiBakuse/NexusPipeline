import { initParticles } from "./effects/particles.js";
import { closeModal } from "./core/modal.js";
import { cycleTheme, initTheme, setNavOpen } from "./core/ui.js";
import { enterPage } from "./core/state.js";
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
  const target = event.target.closest("[data-action]");
  if (!target) return;
  const handler = allActions[target.dataset.action];
  if (handler) handler(target, event);
});

document.addEventListener("input", event => {
  if (event.target?.id === "sm-root") syncScriptGhostState();
});

window.addEventListener("hashchange", route);
window.addEventListener("resize", () => {
  if (window.innerWidth > 820) setNavOpen(false);
});
window.addEventListener("DOMContentLoaded", () => {
  initTheme();
  initParticles();
  route();
});
