import { api } from "../core/api.js";
import { esc, statusBadge } from "../core/format.js";
import { isCurrent, schedule } from "../core/state.js";
import { navActive, render, setTopbarTitle, startCountdown } from "../core/ui.js";

function runningMarkup(running) {
  if (!running.length) return '<div class="empty"><strong>暂无运行任务</strong>当前没有正在运行的脚本或调度队列。</div>';
  return `<div class="table-scroll"><table class="data-table"><thead><tr><th>任务</th><th>类型</th><th>模式</th><th>进度</th><th>状态</th></tr></thead><tbody>
    ${running.map(record => `<tr>
      <td><strong>${esc(record.targetName)}</strong></td>
      <td>${record.kind === "queue" ? "调度队列" : "脚本实例"}</td>
      <td>${record.mode === "auto" ? "自动" : "手动"}</td>
      <td>${esc(record.currentScriptName || "-")} ${esc(record.currentStatus || "")}<br><span class="muted">第 ${record.currentAttempt}/${record.currentMaxAttempts} 次</span></td>
      <td>${statusBadge(record.status)}</td>
    </tr>`).join("")}
  </tbody></table></div>`;
}

function pluginMarkup(status, stats) {
  return (status.plugins || []).map(plugin => `<article class="plugin-card">
    <div class="p-name">${esc(plugin.displayName)}</div>
    <div class="p-ver">${esc(plugin.version)} · ${esc(plugin.description || "本地插件")}</div>
    <div>${plugin.enabled ? '<span class="badge ok">已启用</span>' : '<span class="badge muted">已禁用</span>'}</div>
    ${plugin.name === "notify" ? `<div class="qk-row">已启用通知 ${plugin.enabled ? '<span class="badge ok">开</span>' : '<span class="badge muted">关</span>'}<br><b>${stats.enabledScripts ?? 0}</b> 个脚本实例 / <b>${stats.enabledQueues ?? 0}</b> 个调度队列</div>` : ""}
  </article>`).join("");
}

export async function pageDashboard(token) {
  if (!isCurrent("dashboard", token)) return;
  navActive("dashboard");
  setTopbarTitle("仪表盘");
  let status;
  try {
    status = await api("GET", "/api/status");
  } catch (error) {
    if (isCurrent("dashboard", token)) render(`<div class="empty"><strong>无法连接服务</strong>${esc(error.message)}</div>`);
    return;
  }
  if (!isCurrent("dashboard", token)) return;
  const next = status.nextSchedule;
  const stats = status.notifyStats || {};
  render(`<div class="page-head">
    <div><div class="eyebrow">OPERATIONS OVERVIEW</div><h2>仪表盘</h2><p class="page-kicker">查看当前运行状态、调度概览和通知能力。</p></div>
  </div>
  <section class="stat-grid" aria-label="运行概览">
    <div class="stat stat-accent" data-testid="stat-scripts"><div class="num">${status.scriptCount ?? 0}</div><div class="lbl">脚本实例</div></div>
    <div class="stat" data-testid="stat-queues"><div class="num">${status.queueCount ?? 0}</div><div class="lbl">调度队列</div></div>
    <div class="stat" data-testid="stat-next"><div class="num" id="next-q">${next ? esc(next.queueName) : "无"}</div><div class="lbl" id="next-cd">${next ? "正在计算倒计时" : "下一调度队列"}</div></div>
    <div class="stat" data-testid="stat-version"><div class="num">${esc(status.version || "0.0.0")}</div><div class="lbl">当前版本</div></div>
  </section>
  <section class="card" data-testid="running-panel"><div class="section-heading"><h3>正在运行</h3><span class="muted">${(status.running || []).length} 个活动任务</span></div>${runningMarkup(status.running || [])}</section>
  <section class="card"><div class="section-heading"><h3>插件能力</h3><span class="muted">本地扩展状态</span></div><div class="plugin-grid">${pluginMarkup(status, stats) || '<div class="empty">暂无已加载插件</div>'}</div></section>`);
  if (next) startCountdown("next-cd", next.time);
  schedule(() => pageDashboard(token), 3000, "dashboard", token);
}
