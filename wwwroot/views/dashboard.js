import { api } from "../core/api.js";
import { pageHeader, systemActionCard } from "../core/forms.js";
import { esc, statusBadge } from "../core/format.js";
import { isCurrent, schedule } from "../core/state.js";
import { navActive, render, setTopbarTitle, startSystemActionCountdown } from "../core/ui.js";
import { pluginSlotMarkup, renderPluginSlots } from "../core/plugin-slots.js";
import { notifyPluginPageUpdated } from "../core/plugin-runtime.js";
import { formatList, t } from "../core/i18n.js";

function runningMarkup(running) {
  if (!running.length) return `<div class="empty"><strong>${t("dashboard.idle")}</strong><span>${t("dashboard.running.empty")}</span><a class="back-link" href="#/dispatch">${t("dashboard.go_to_dispatch")}</a></div>`;
  const records = running.map(record => `<article class="running-record">
      <div class="running-record-head"><strong>${esc(record.targetName)}</strong>${statusBadge(record.status)}</div>
      <div class="running-record-meta"><span>${t(record.kind === "queue" ? "common.schedule_queues" : "common.script_instance")}</span><span>${t(record.mode === "auto" ? "common.automatic" : "common.manual")}</span></div>
      <div class="running-record-progress">${esc(record.currentScriptName || "-")} ${esc(record.currentStatus || "")}<br><span class="muted">${t("dashboard.running.attempt", { attempt: record.currentAttempt, max: record.currentMaxAttempts })}</span>${record.persistenceWarning ? `<br><span class="badge warn">${esc(t("dashboard.persistence_warning", { label: t("common.history.persistence_warning"), warning: record.persistenceWarning }))}</span>` : ""}</div>
    </article>`).join("");
  return `<div class="table-scroll running-table"><table class="data-table"><thead><tr><th scope="col">${t("common.task")}</th><th scope="col">${t("dashboard.type")}</th><th scope="col">${t("dashboard.mode")}</th><th scope="col">${t("dashboard.progress")}</th><th scope="col">${t("common.status")}</th></tr></thead><tbody>
    ${running.map(record => `<tr>
      <td><strong>${esc(record.targetName)}</strong></td>
      <td>${t(record.kind === "queue" ? "common.schedule_queues" : "common.script_instance")}</td>
      <td>${t(record.mode === "auto" ? "common.automatic" : "common.manual")}</td>
      <td>${esc(record.currentScriptName || "-")} ${esc(record.currentStatus || "")}<br><span class="muted">${t("dashboard.running.attempt", { attempt: record.currentAttempt, max: record.currentMaxAttempts })}</span></td>
      <td>${statusBadge(record.status)}</td>
    </tr>`).join("")}
  </tbody></table></div><div class="running-records">${records}</div>`;
}

function pluginMarkup(status) {
  const disabled = (status.plugins || []).filter(plugin => !plugin.configuredEnabled);
  if (!disabled.length) return "";
  return `<div class="dashboard-system-note" data-testid="plugin-health"><p>${t("dashboard.plugins.disabled_summary", { count: disabled.length, plugins: formatList(disabled.map(plugin => esc(plugin.displayName))) })}</p><a class="back-link" href="#/plugins">${t("dashboard.view_plugins")}</a></div>`;
}

function setVersionLabel(version) {
  const el = document.querySelector("#app-version");
  if (el) el.textContent = `${t("common.current_version")} · ${version || "0.0.0"}`;
}

function runningPanelMarkup(status) {
  return `<div class="section-heading"><h3>${t("common.running")}</h3><span class="muted">${(status.running || []).length} ${t("dashboard.active_tasks")}</span></div>${runningMarkup(status.running || [])}`;
}

function pluginPanelMarkup(status) {
  const markup = pluginMarkup(status);
  return markup || "";
}

function statePanelMarkup(status) {
  const running = status.running || [];
  const active = running.length > 0;
  return `<section id="dashboard-state" class="dashboard-state ${active ? "running" : "idle"}" data-testid="dashboard-state" aria-live="polite">
    <div class="dashboard-state-copy"><div class="state-label">${t(active ? "common.running" : "dashboard.system_idle")}</div><h3>${t(active ? "dashboard.task_in_progress" : "dashboard.everything_is_ready")}</h3><p>${active ? t("dashboard.running.summary", { count: running.length }) : t("dashboard.running.empty_help")}</p></div>
  </section>`;
}

export async function pageDashboard(token) {
  if (!isCurrent("dashboard", token)) return;
  navActive("dashboard");
  setTopbarTitle(t("dashboard.dashboard"));
  let status;
  try {
    status = await api("GET", "/api/status");
  } catch (error) {
    if (isCurrent("dashboard", token) && !document.querySelector('[data-testid="dashboard-state"]')) {
      render(`<div class="empty"><strong>${t("dashboard.connection.unavailable")}</strong>${esc(error.message)}</div>`);
    }
    return;
  }
  if (!isCurrent("dashboard", token)) return;
  setVersionLabel(status.version);
  const isInitialRender = !document.querySelector('[data-testid="dashboard-state"]');
  if (isInitialRender) {
    render(pageHeader(t("dashboard.run_overview"), t("dashboard.dashboard"), t("dashboard.overview.help"))
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
      if (label) label.textContent = t(active ? "common.running" : "dashboard.system_idle");
      if (heading) heading.textContent = t(active ? "dashboard.task_in_progress" : "dashboard.everything_is_ready");
      if (copy) copy.textContent = active ? t("dashboard.running.summary", { count: (status.running || []).length }) : t("dashboard.running.empty_help");
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
