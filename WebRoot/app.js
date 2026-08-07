import { initParticles } from "./effects/particles.js";
import { closeModal, cycleTheme, initTheme, setNavOpen } from "./core/ui.js";
import { enterPage, state } from "./core/state.js";
import {
  deleteQueue, deleteScript, deleteUser, editConfigAction, editUserConfig, openQueueModal, openScriptModal,
  openUserModal, pageQueues, pageScriptUsers, pageScripts, queueAddTask, queueAddTimeSet, queueMoveTask,
  queueRemoveTask, queueRemoveTimeSet, saveQueue, saveScript, saveUser, syncScriptGhostState,
} from "./views/catalog.js";
import { pageDashboard } from "./views/dashboard.js";
import {
  cancelRun, dispatchQueue, dispatchScript, historyDetail, pageDispatch, pageHistory, pagePluginConfig,
  pagePlugins, pageSettings, saveNotifySettings, saveSecret, saveSettings, testNotify, togglePanel,
  togglePlugin,
} from "./views/operations.js";

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
  const action = target.dataset.action;
  const id = target.dataset.id;
  const name = target.dataset.name;
  switch (action) {
    case "open-nav": setNavOpen(true); break;
    case "close-nav": setNavOpen(false); break;
    case "toggle-theme": cycleTheme(); break;
    case "close-modal": closeModal(); break;
    case "open-script-modal": openScriptModal(id || ""); break;
    case "edit-script": openScriptModal(id); break;
    case "delete-script": deleteScript(id, name); break;
    case "manage-users": location.hash = "#/scripts/" + id + "/users"; break;
    case "open-user-modal": openUserModal(id); break;
    case "edit-user": openUserModal(id, name); break;
    case "save-user": saveUser(); break;
    case "delete-user": deleteUser(id, name); break;
    case "edit-user-config": editUserConfig(id, name); break;
    case "edit-config-done": editConfigAction(id, name, "done"); break;
    case "edit-config-cancel": editConfigAction(id, name, "cancel"); break;
    case "save-script": saveScript(); break;
    case "open-queue-modal": openQueueModal(id || ""); break;
    case "edit-queue": openQueueModal(id); break;
    case "delete-queue": deleteQueue(id, name); break;
    case "save-queue": saveQueue(); break;
    case "add-time-set": queueAddTimeSet(); break;
    case "remove-time-set": queueRemoveTimeSet(+target.dataset.index); break;
    case "add-task": queueAddTask(); break;
    case "remove-task": queueRemoveTask(+target.dataset.index); break;
    case "move-task-up": queueMoveTask(+target.dataset.index, -1); break;
    case "move-task-down": queueMoveTask(+target.dataset.index, 1); break;
    case "dispatch-script": dispatchScript(); break;
    case "dispatch-queue": dispatchQueue(); break;
    case "cancel-run": cancelRun(id); break;
    case "history-detail": historyDetail(id); break;
    case "plugin-config": location.hash = "#/plugins/" + target.dataset.name; break;
    case "toggle-plugin": togglePlugin(target.dataset.name, target.dataset.enabled === "true"); break;
    case "toggle-panel": togglePanel(target.dataset.panel, target); break;
    case "save-notify-settings": saveNotifySettings(); break;
    case "save-secret": saveSecret(target.dataset.secret, target.dataset.input); break;
    case "test-notify": testNotify(); break;
    case "save-settings": saveSettings(); break;
  }
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
