import { api } from "../core/api.js";
import { pageHeader, systemActionCard } from "../core/forms.js";
import { esc, statusBadge } from "../core/format.js";
import { isCurrent, schedule } from "../core/state.js";
import { navActive, render, setTopbarTitle, startCountdown, startSystemActionCountdown, stopCountdown } from "../core/ui.js";

function runningMarkup(running) {
  if (!running.length) return '<div class="empty"><strong>暂无运行任务</strong>当前没有正在运行的脚本或调度队列。</div>';
  const records = running.map(record => `<article class="running-record">
      <div class="running-record-head"><strong>${esc(record.targetName)}</strong>${statusBadge(record.status)}</div>
      <div class="running-record-meta"><span>${record.kind === "queue" ? "调度队列" : "脚本实例"}</span><span>${record.mode === "auto" ? "自动" : "手动"}</span></div>
      <div class="running-record-progress">${esc(record.currentScriptName || "-")} ${esc(record.currentStatus || "")}<br><span class="muted">第 ${record.currentAttempt}/${record.currentMaxAttempts} 次</span></div>
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

function pluginMarkup(status, stats) {
  return (status.plugins || []).map(plugin => `<article class="plugin-card">
    <div class="p-name">${esc(plugin.displayName)}</div>
    <div class="p-ver">${esc(plugin.version)} · ${esc(plugin.description || "本地插件")}</div>
    <div>${plugin.enabled ? '<span class="badge ok">已启用</span>' : '<span class="badge muted">已禁用</span>'}</div>
    ${plugin.name === "notify" ? `<div class="qk-row">已启用通知 ${plugin.enabled ? '<span class="badge ok">开</span>' : '<span class="badge muted">关</span>'}<br><b>${stats.enabledScripts ?? 0}</b> 个脚本实例 / <b>${stats.enabledQueues ?? 0}</b> 个调度队列</div>` : ""}
  </article>`).join("");
}

function statGridMarkup(status, next) {
  return `<section class="stat-grid stat-grid-operational" aria-label="运行概览">
    <div class="stat stat-accent" data-testid="stat-scripts"><div class="num">${status.scriptCount ?? 0}</div><div class="lbl">脚本实例</div></div>
    <div class="stat" data-testid="stat-queues"><div class="num">${status.queueCount ?? 0}</div><div class="lbl">调度队列</div></div>
    <div class="stat" data-testid="stat-running"><div class="num">${(status.running || []).length}</div><div class="lbl">正在运行</div></div>
  </section>
  <section class="stat next-schedule-card" data-testid="stat-next" aria-label="下一调度队列">
    <div class="next-schedule-main"><div class="eyebrow">下一次调度</div><div class="num" id="next-q">${next ? "正在计算倒计时" : "无"}</div></div>
    <div class="next-schedule-detail"><strong class="next-q-label" id="next-q-label">下一调度队列：${next ? esc(next.queueName || "未命名队列") : "无"}</strong><span class="muted">${next ? "按启用的定时队列计算" : "暂无已启用的定时队列"}</span></div>
  </section>`;
}

function setVersionLabel(version) {
  const el = document.querySelector("#app-version");
  if (el) el.textContent = `当前版本 · ${version || "0.0.0"}`;
}

function runningPanelMarkup(status) {
  return `<div class="section-heading"><h3>正在运行</h3><span class="muted">${(status.running || []).length} 个活动任务</span></div>${runningMarkup(status.running || [])}`;
}

function pluginPanelMarkup(status, stats) {
  return `<div class="section-heading"><h3>插件能力</h3><span class="muted">本地扩展状态</span></div><div class="plugin-grid">${pluginMarkup(status, stats) || '<div class="empty">暂无已加载插件</div>'}</div>`;
}

export async function pageDashboard(token) {
  if (!isCurrent("dashboard", token)) return;
  navActive("dashboard");
  setTopbarTitle("仪表盘");
  let status;
  try {
    status = await api("GET", "/api/status");
  } catch (error) {
    if (isCurrent("dashboard", token) && !document.querySelector('[data-testid="stat-scripts"]')) {
      render(`<div class="empty"><strong>无法连接服务</strong>${esc(error.message)}</div>`);
    }
    return;
  }
  if (!isCurrent("dashboard", token)) return;
  const next = status.nextSchedule;
  const stats = status.notifyStats || {};
  setVersionLabel(status.version);
  if (!document.querySelector('[data-testid="stat-scripts"]')) {
    render(pageHeader("运行概览", "仪表盘", "查看当前运行状态、调度概览和通知能力。")
      + statGridMarkup(status, next)
      + `<div id="system-action-area">${systemActionCard(status.systemAction)}</div>
      <section class="card" data-testid="running-panel">${runningPanelMarkup(status)}</section>
      <section class="card" id="dashboard-plugin-panel">${pluginPanelMarkup(status, stats)}</section>`);
    if (next) startCountdown("next-q", next.time); else stopCountdown();
  } else {
    // 局部更新（v0.6.7+）：不整页重渲染，避免倒计时定时器反复重建与滚动/焦点重置；区域缺失时静默跳过。
    const setNum = (selector, text) => {
      const el = document.querySelector(selector);
      if (el) el.textContent = text;
    };
    setNum('.stat[data-testid="stat-scripts"] .num', status.scriptCount ?? 0);
    setNum('.stat[data-testid="stat-queues"] .num', status.queueCount ?? 0);
    setNum('.stat[data-testid="stat-running"] .num', (status.running || []).length);
    const nextEl = document.querySelector("#next-q");
    const nextLabel = document.querySelector("#next-q-label");
    if (nextEl) {
      if (next) {
        if (nextLabel) nextLabel.textContent = "下一调度队列：" + (next.queueName || "未命名队列");
        startCountdown("next-q", next.time);
      } else {
        stopCountdown();
        nextEl.textContent = "无";
        if (nextLabel) nextLabel.textContent = "下一调度队列：无";
      }
    }
    const sysArea = document.querySelector("#system-action-area");
    if (sysArea) sysArea.innerHTML = systemActionCard(status.systemAction);
    const runningPanel = document.querySelector('[data-testid="running-panel"]');
    if (runningPanel) runningPanel.innerHTML = runningPanelMarkup(status);
    const pluginPanel = document.querySelector("#dashboard-plugin-panel");
    if (pluginPanel) pluginPanel.innerHTML = pluginPanelMarkup(status, stats);
  }
  startSystemActionCountdown();
  schedule(() => pageDashboard(token), 3000, "dashboard", token);
}
