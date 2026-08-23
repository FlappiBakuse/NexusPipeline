import { api } from "../core/api.js";
import { pageHeader, systemActionCard } from "../core/forms.js";
import { esc, statusBadge } from "../core/format.js";
import { isCurrent, schedule } from "../core/state.js";
import { navActive, render, setTopbarTitle, startSystemActionCountdown } from "../core/ui.js";

function runningMarkup(running) {
  if (!running.length) return '<div class="empty"><strong>当前空闲</strong><span>没有正在运行的脚本或调度队列。</span><a class="back-link" href="#/dispatch">前往调度中心</a></div>';
  const records = running.map(record => `<article class="running-record">
      <div class="running-record-head"><strong>${esc(record.targetName)}</strong>${statusBadge(record.status)}</div>
      <div class="running-record-meta"><span>${record.kind === "queue" ? "调度队列" : "脚本实例"}</span><span>${record.mode === "auto" ? "自动" : "手动"}</span></div>
      <div class="running-record-progress">${esc(record.currentScriptName || "-")} ${esc(record.currentStatus || "")}<br><span class="muted">第 ${record.currentAttempt}/${record.currentMaxAttempts} 次</span>${record.persistenceWarning ? `<br><span class="badge warn">历史保存警告：${esc(record.persistenceWarning)}</span>` : ""}</div>
    </article>`).join("");
  return `<div class="table-scroll running-table"><table class="data-table"><thead><tr><th scope="col">任务</th><th scope="col">类型</th><th scope="col">模式</th><th scope="col">进度</th><th scope="col">状态</th></tr></thead><tbody>
    ${running.map(record => `<tr>
      <td><strong>${esc(record.targetName)}</strong></td>
      <td>${record.kind === "queue" ? "调度队列" : "脚本实例"}</td>
      <td>${record.mode === "auto" ? "自动" : "手动"}</td>
      <td>${esc(record.currentScriptName || "-")} ${esc(record.currentStatus || "")}<br><span class="muted">第 ${record.currentAttempt}/${record.currentMaxAttempts} 次</span></td>
      <td>${statusBadge(record.status)}</td>
    </tr>`).join("")}
  </tbody></table></div><div class="running-records">${records}</div>`;
}

function pluginMarkup(status) {
  const disabled = (status.plugins || []).filter(plugin => !plugin.enabled);
  if (!disabled.length) return "";
  return `<div class="dashboard-system-note" data-testid="plugin-health"><p>${disabled.length} 个插件当前已禁用：${disabled.map(plugin => esc(plugin.displayName)).join("、")}</p><a class="back-link" href="#/plugins">查看插件</a></div>`;
}

function setVersionLabel(version) {
  const el = document.querySelector("#app-version");
  if (el) el.textContent = `当前版本 · ${version || "0.0.0"}`;
}

function runningPanelMarkup(status) {
  return `<div class="section-heading"><h3>正在运行</h3><span class="muted">${(status.running || []).length} 个活动任务</span></div>${runningMarkup(status.running || [])}`;
}

function pluginPanelMarkup(status) {
  const markup = pluginMarkup(status);
  return markup || "";
}

function statePanelMarkup(status) {
  const running = status.running || [];
  const active = running.length > 0;
  return `<section id="dashboard-state" class="dashboard-state ${active ? "running" : "idle"}" data-testid="dashboard-state" aria-live="polite">
    <div class="dashboard-state-copy"><div class="state-label">${active ? "正在运行" : "系统空闲"}</div><h3>${active ? "任务正在执行" : "一切准备就绪"}</h3><p>${active ? `当前有 ${running.length} 个活动任务，状态会自动更新。` : "当前没有活动任务，可以从调度中心手动执行脚本或队列。"}</p></div>
  </section>`;
}

export async function pageDashboard(token) {
  if (!isCurrent("dashboard", token)) return;
  navActive("dashboard");
  setTopbarTitle("仪表盘");
  let status;
  try {
    status = await api("GET", "/api/status");
  } catch (error) {
    if (isCurrent("dashboard", token) && !document.querySelector('[data-testid="dashboard-state"]')) {
      render(`<div class="empty"><strong>无法连接服务</strong>${esc(error.message)}</div>`);
    }
    return;
  }
  if (!isCurrent("dashboard", token)) return;
  setVersionLabel(status.version);
  if (!document.querySelector('[data-testid="dashboard-state"]')) {
    render(pageHeader("运行概览", "仪表盘", "查看当前运行状态、调度概览和通知能力。")
      + statePanelMarkup(status)
      + `<div id="system-action-area">${systemActionCard(status.systemAction)}</div>
      <section class="content-section list-surface" data-testid="running-panel">${runningPanelMarkup(status)}</section>
      <section class="content-section" id="dashboard-plugin-panel" hidden>${pluginPanelMarkup(status)}</section>`);
  } else {
    // 局部更新（v0.6.7+）：不整页重渲染，避免滚动/焦点重置；区域缺失时静默跳过。
    const statePanel = document.querySelector("#dashboard-state");
    if (statePanel) {
      const active = (status.running || []).length > 0;
      statePanel.classList.toggle("running", active);
      statePanel.classList.toggle("idle", !active);
      const label = statePanel.querySelector(".state-label");
      const heading = statePanel.querySelector("h3");
      const copy = statePanel.querySelector("p");
      if (label) label.textContent = active ? "正在运行" : "系统空闲";
      if (heading) heading.textContent = active ? "任务正在执行" : "一切准备就绪";
      if (copy) copy.textContent = active ? `当前有 ${(status.running || []).length} 个活动任务，状态会自动更新。` : "当前没有活动任务，可以从调度中心手动执行脚本或队列。";
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
  startSystemActionCountdown();
  schedule(() => pageDashboard(token), 3000, "dashboard", token);
}
