import { api } from "../core/api.js";
import { pageHeader, systemActionCard } from "../core/forms.js";
import { esc, statusBadge } from "../core/format.js";
import { isCurrent, schedule } from "../core/state.js";
import { navActive, render, setTopbarTitle, startSystemActionCountdown } from "../core/ui.js";
import { pluginSlotMarkup, renderPluginSlots } from "../core/plugin-slots.js";
import { notifyPluginPageUpdated } from "../core/plugin-runtime.js";
import { t } from "../core/i18n.js";

function runningMarkup(running) {
  if (!running.length) return `<div class="empty"><strong>${t("ui.idle")}</strong><span>${t("ui.no_scripts_or_queues_are_running")}</span><a class="back-link" href="#/dispatch">${t("ui.go_to_dispatch")}</a></div>`;
  const records = running.map(record => `<article class="running-record">
      <div class="running-record-head"><strong>${esc(record.targetName)}</strong>${statusBadge(record.status)}</div>
      <div class="running-record-meta"><span>${t(record.kind === "queue" ? "ui.schedule_queues" : "ui.script_instance")}</span><span>${t(record.mode === "auto" ? "ui.automatic" : "ui.manual")}</span></div>
      <div class="running-record-progress">${esc(record.currentScriptName || "-")} ${esc(record.currentStatus || "")}<br><span class="muted">${t("ui.attempt_value_value", { attempt: record.currentAttempt, max: record.currentMaxAttempts })}</span>${record.persistenceWarning ? `<br><span class="badge warn">${t("ui.history_persistence_warning")}：${esc(record.persistenceWarning)}</span>` : ""}</div>
    </article>`).join("");
  return `<div class="table-scroll running-table"><table class="data-table"><thead><tr><th scope="col">${t("ui.task")}</th><th scope="col">${t("ui.type")}</th><th scope="col">${t("ui.mode")}</th><th scope="col">${t("ui.progress")}</th><th scope="col">${t("ui.status")}</th></tr></thead><tbody>
    ${running.map(record => `<tr>
      <td><strong>${esc(record.targetName)}</strong></td>
      <td>${t(record.kind === "queue" ? "ui.schedule_queues" : "ui.script_instance")}</td>
      <td>${t(record.mode === "auto" ? "ui.automatic" : "ui.manual")}</td>
      <td>${esc(record.currentScriptName || "-")} ${esc(record.currentStatus || "")}<br><span class="muted">${t("ui.attempt_value_value", { attempt: record.currentAttempt, max: record.currentMaxAttempts })}</span></td>
      <td>${statusBadge(record.status)}</td>
    </tr>`).join("")}
  </tbody></table></div><div class="running-records">${records}</div>`;
}

function pluginMarkup(status) {
  const disabled = (status.plugins || []).filter(plugin => !plugin.configuredEnabled);
  if (!disabled.length) return "";
  return `<div class="dashboard-system-note" data-testid="plugin-health"><p>${t("ui.value_plugin_s_are_currently_disabled", { count: disabled.length })}${disabled.map(plugin => esc(plugin.displayName)).join("、")}</p><a class="back-link" href="#/plugins">${t("ui.view_plugins")}</a></div>`;
}

function setVersionLabel(version) {
  const el = document.querySelector("#app-version");
  if (el) el.textContent = `${t("ui.current_version")} · ${version || "0.0.0"}`;
}

function runningPanelMarkup(status) {
  return `<div class="section-heading"><h3>${t("ui.running")}</h3><span class="muted">${(status.running || []).length} ${t("ui.active_tasks")}</span></div>${runningMarkup(status.running || [])}`;
}

function pluginPanelMarkup(status) {
  const markup = pluginMarkup(status);
  return markup || "";
}

function statePanelMarkup(status) {
  const running = status.running || [];
  const active = running.length > 0;
  return `<section id="dashboard-state" class="dashboard-state ${active ? "running" : "idle"}" data-testid="dashboard-state" aria-live="polite">
    <div class="dashboard-state-copy"><div class="state-label">${t(active ? "ui.running" : "ui.system_idle")}</div><h3>${t(active ? "ui.task_in_progress" : "ui.everything_is_ready")}</h3><p>${active ? t("ui.value_active_tasks_status_updates_automatically", { count: running.length }) : t("ui.there_are_no_active_tasks_start_a_script_or_queue_manually_from_dispatch")}</p></div>
  </section>`;
}

export async function pageDashboard(token) {
  if (!isCurrent("dashboard", token)) return;
  navActive("dashboard");
  setTopbarTitle(t("ui.dashboard"));
  let status;
  try {
    status = await api("GET", "/api/status");
  } catch (error) {
    if (isCurrent("dashboard", token) && !document.querySelector('[data-testid="dashboard-state"]')) {
      render(`<div class="empty"><strong>${t("ui.unable_to_connect_to_the_service")}</strong>${esc(error.message)}</div>`);
    }
    return;
  }
  if (!isCurrent("dashboard", token)) return;
  setVersionLabel(status.version);
  const isInitialRender = !document.querySelector('[data-testid="dashboard-state"]');
  if (isInitialRender) {
    render(pageHeader(t("ui.run_overview"), t("ui.dashboard"), t("ui.view_current_run_status_scheduling_overview_and_notification_capabilities"))
      + pluginSlotMarkup("dashboard.cards", "dashboard.cards")
      + statePanelMarkup(status)
      + `<div id="system-action-area">${systemActionCard(status.systemAction)}</div>
      <section class="content-section list-surface" data-testid="running-panel">${runningPanelMarkup(status)}</section>
      ${pluginSlotMarkup("dashboard.after-running", "dashboard.after-running")}
      <section class="content-section" id="dashboard-plugin-panel" hidden>${pluginPanelMarkup(status)}</section>`);
  } else {
    // 局部更新：不整页重渲染，避免滚动/焦点重置；区域缺失时静默跳过。
    const statePanel = document.querySelector("#dashboard-state");
    if (statePanel) {
      const active = (status.running || []).length > 0;
      statePanel.classList.toggle("running", active);
      statePanel.classList.toggle("idle", !active);
      const label = statePanel.querySelector(".state-label");
      const heading = statePanel.querySelector("h3");
      const copy = statePanel.querySelector("p");
      if (label) label.textContent = t(active ? "ui.running" : "ui.system_idle");
      if (heading) heading.textContent = t(active ? "ui.task_in_progress" : "ui.everything_is_ready");
      if (copy) copy.textContent = active ? t("ui.value_active_tasks_status_updates_automatically", { count: (status.running || []).length }) : t("ui.there_are_no_active_tasks_start_a_script_or_queue_manually_from_dispatch");
    }
    const sysArea = document.querySelector("#system-action-area");
    if (sysArea) sysArea.innerHTML = systemActionCard(status.systemAction);
    const runningPanel = document.querySelector('[data-testid="running-panel"]');
    if (runningPanel) runningPanel.innerHTML = runningPanelMarkup(status);
    const pluginPanel = document.querySelector("#dashboard-plugin-panel");
    if (pluginPanel) {
      pluginPanel.innerHTML = pluginPanelMarkup(status);
      pluginPanel.hidden = !pluginMarkup(status);
    }
  }
  await renderPluginSlots(document.querySelector("#view"));
  if (!isInitialRender) {
    await notifyPluginPageUpdated({
      hash: "dashboard",
      page: "dashboard",
      segments: ["dashboard"],
      token,
      container: document.querySelector("#view"),
    });
  }
  startSystemActionCountdown();
  schedule(() => pageDashboard(token), 3000, "dashboard", token);
}
