import { api } from "../core/api.js";
import { esc, finalStatusOf, fmtTime, statusBadge } from "../core/format.js";
import { pageHeader } from "../core/forms.js";
import { icon } from "../core/icons.js";
import { isCurrent, state } from "../core/state.js";
import { modalShell, showModal } from "../core/modal.js";
import { navActive, render, setTopbarTitle, toast, withBusy } from "../core/ui.js";

let historyDays = 30;
let historyDates = [];
let historySelectedDate = "";
let historyDir = "";
const HISTORY_DAY_OPTIONS = [7, 15, 30, 60, 90, 120, 180];

const pad = n => String(n).padStart(2, "0");

/** 「2026年08月21日」样式的日期文本（date 参数形如 2026-08-21）。 */
function fmtDateCN(dateStr) {
  const parts = String(dateStr || "").split("-");
  return parts.length === 3 ? `${parts[0]}年${parts[1]}月${parts[2]}日` : esc(dateStr);
}

/** 「2026年08月21日 04:05:00」样式的完整时间文本。 */
function fmtDateTimeCN(value) {
  const d = new Date(value);
  if (!value || isNaN(d.getTime())) return "-";
  return `${d.getFullYear()}年${pad(d.getMonth() + 1)}月${pad(d.getDate())}日 ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

/** 记录条状态徽章（参考图2）：成功=✓ 完成、失败=✕ 失败：原因、部分完成/已取消=警示色。 */
function entryBadge(record) {
  const status = finalStatusOf(record);
  if (status === "success") return '<span class="badge ok">✓ 完成</span>';
  if (status === "partial") return '<span class="badge warn">⚠ 部分完成</span>';
  if (status === "cancelled") return '<span class="badge warn">已取消</span>';
  const reason = record.resultDetail ? `：${record.resultDetail}` : "";
  return `<span class="badge bad" title="${esc(reason)}">✕ 失败${esc(reason)}</span>`;
}

function daysAction() {
  return `<div class="history-days-box"><label class="field-label" for="history-days">天数范围</label><select id="history-days" data-action="history-days" data-testid="history-days">${HISTORY_DAY_OPTIONS.map(days => `<option value="${days}" ${days === historyDays ? "selected" : ""}>${days} 天</option>`).join("")}</select></div>`;
}

function dateRowsMarkup() {
  return historyDates.map(date => `<button class="history-date-row${date.date === historySelectedDate ? " active" : ""}" type="button" data-action="history-date" data-date="${esc(date.date)}" data-testid="history-date" aria-pressed="${date.date === historySelectedDate ? "true" : "false"}">${icon("chevronRight")}<span>${fmtDateCN(date.date)}</span><span class="muted">${date.count} 条</span></button>`).join("");
}

function entryMarkup(record) {
  const queue = record.queueName ? ` · ${esc(record.queueName)}` : "";
  const filePath = historyDir && historySelectedDate ? `${historyDir}\\${historySelectedDate}\\${esc(record.logFile || "")}` : esc(record.logFile || "");
  return `<button class="history-entry history-status-${esc(finalStatusOf(record))}" type="button" data-action="history-detail" data-id="${esc(record.id)}" data-testid="history-entry">
    <span class="history-entry-bar" aria-hidden="true"></span>
    <span class="history-entry-main">
      <span class="history-entry-title"><strong>${fmtDateTimeCN(record.startTime)} · ${esc(record.scriptName)}${queue}</strong>${entryBadge(record)}</span>
      <span class="history-entry-path">${filePath}</span>
    </span>
    <span class="history-entry-arrow" aria-hidden="true">${icon("chevronRight")}</span>
  </button>`;
}

function panelsMarkup(records) {
  return `<section class="card history-panels" data-testid="history-panels">
    <aside class="history-dates-panel">
      <div class="history-panel-head">${icon("calendar")}<h3>运行日期</h3><span class="muted">${historyDates.length} 天</span></div>
      <div class="history-dates-list">${dateRowsMarkup()}</div>
    </aside>
    <section class="history-records-panel">
      <div class="history-panel-head">${icon("queues")}<h3>运行情况</h3><span class="muted" data-testid="history-records-count">${records.length} 条记录</span><button class="history-refresh" type="button" data-action="history-refresh" aria-label="刷新记录" data-testid="history-refresh">${icon("refresh")}</button></div>
      <div class="history-entry-list">${records.length ? records.map(entryMarkup).join("") : '<div class="empty"><strong>该日暂无记录</strong></div>'}</div>
    </section>
  </section>`;
}

export async function pageHistory(token) {
  if (!isCurrent("history", token)) return;
  navActive("history"); setTopbarTitle("历史记录");
  let data;
  try {
    data = await api("GET", `/api/history/dates?days=${historyDays}`);
  } catch (error) {
    if (isCurrent("history", token)) render(`<div class="empty"><strong>加载历史记录失败</strong>${esc(error.message)}</div>`);
    return;
  }
  if (!isCurrent("history", token)) return;
  historyDates = data.dates || [];
  if (!historyDates.some(date => date.date === historySelectedDate)) {
    historySelectedDate = historyDates[0]?.date || "";
  }
  if (!historySelectedDate) {
    render(pageHeader("历史记录", "历史记录", `最近 ${historyDays} 天 · 暂无运行记录`, daysAction()) + '<div class="empty"><strong>暂无历史记录</strong>运行脚本或调度队列后在此查看。</div>');
    return;
  }
  await loadDayRecords(token);
}

/** 拉取选中日期的记录并渲染（无轮询；刷新按钮与切日期共用）。 */
async function loadDayRecords(token) {
  let data;
  try {
    data = await api("GET", `/api/history?date=${encodeURIComponent(historySelectedDate)}`);
  } catch (error) {
    if (isCurrent("history", token)) {
      toast(error.message, "error");
      render(pageHeader("历史记录", "历史记录", `最近 ${historyDays} 天`, daysAction()) + panelsMarkup([]));
    }
    return;
  }
  if (!isCurrent("history", token)) return;
  historyDir = data.historyDir || "";
  const records = data.records || [];
  render(pageHeader("历史记录", "历史记录", `最近 ${historyDays} 天 · 按日期查看运行记录`, daysAction()) + panelsMarkup(records));
}

/** 天数范围切换（v0.8.7+）：扩展为 7/15/30/60/90/120/180 天，切换后重拉日期列表并回落最新日期。 */
export function historyDaysChange(target) {
  const days = Number(target.value) || 30;
  if (days === historyDays) return;
  historyDays = days;
  historySelectedDate = "";
  pageHistory(state.routeToken);
}

/** 左侧日期行点击：切换选中日期并加载当日记录。 */
export async function historySelectDate(target) {
  const date = target.dataset.date;
  if (!date || date === historySelectedDate) return;
  historySelectedDate = date;
  await loadDayRecords(state.routeToken);
}

/** 右侧刷新按钮：重拉当前选中日期的记录。 */
export async function historyRefresh() {
  await loadDayRecords(state.routeToken);
}

function historyLogMarkup(id, attemptKey, logInfo, label) {
  const total = logInfo?.logTotalLines || 0;
  const full = logInfo?.logText != null;
  const tailNote = total > 200 && !full ? "，仅显示尾部 200 行" : "";
  const action = total > 200 && !full
    ? `<div class="history-log-actions"><span class="muted">日志较长，默认只加载尾部</span><button class="ghost sm" type="button" data-action="history-full-log" data-id="${esc(id)}" data-attempt="${esc(attemptKey)}">查看完整日志</button></div>`
    : "";
  const text = full ? logInfo.logText : (logInfo?.logTail || "（无脚本日志）");
  return `<div class="history-log" data-history-log data-attempt="${esc(attemptKey)}"><div class="qk-row" data-history-log-meta>${label}${logInfo ? `，${total} 行${tailNote}` : ""}</div>${action}<pre class="logbox" data-history-log-body>${esc(text)}</pre></div>`;
}

export async function historyDetail(id) {
  try {
    const data = await api("GET", "/api/history/detail?id=" + encodeURIComponent(id));
    const record = data.record;
    if (!record) return;
    const attempts = (record.attemptDetails || []).map(attempt => {
      const logInfo = (data.attemptLogs || []).find(l => l.number === attempt.number);
      return `<div class="subsection"><h3>第 ${attempt.number} 次尝试：${attempt.status === "success" ? "成功" : attempt.status === "cancelled" ? "已取消" : "失败"}</h3><div class="detail"><div class="kv"><span class="k">原因</span><span>${esc(attempt.reason || "-")}</span></div><div class="kv"><span class="k">时间</span><span>${esc(fmtTime(attempt.startTime))} - ${esc(fmtTime(attempt.endTime))}</span></div></div>${historyLogMarkup(id, String(attempt.number), logInfo, `脚本日志（第 ${attempt.number} 次尝试）`)} </div>`;
    }).join("");
    const body = `<div class="history-summary"><div><span class="k">结果</span><span class="v">${statusBadge(finalStatusOf(record))}</span></div><div><span class="k">运行模式</span><span class="v">${record.mode === "auto" ? "自动运行" : "手动运行"}</span></div><div><span class="k">尝试次数</span><span class="v">${record.attempts || 0} / ${record.maxAttempts || "-"}</span></div><div><span class="k">开始时间</span><span class="v">${esc(fmtTime(record.startTime))}</span></div></div><div class="detail"><div class="kv"><span class="k">开始</span><span>${esc(fmtTime(record.startTime))}</span></div><div class="kv"><span class="k">结束</span><span>${esc(fmtTime(record.endTime))}</span></div><div class="kv"><span class="k">模式</span><span>${record.mode === "auto" ? "自动运行" : "手动运行"}</span></div>${record.userName ? `<div class="kv"><span class="k">用户</span><span>${esc(record.userName)}</span></div>` : ""}<div class="kv"><span class="k">重试</span><span>${record.attempts || 0} / ${record.maxAttempts || "-"} 次</span></div><div class="kv"><span class="k">结果</span><span>${statusBadge(finalStatusOf(record))} ${esc(record.resultDetail)}</span></div></div>${attempts}`;
    showModal(modalShell(`${esc(record.scriptName)} 运行详情`, body, '<button class="ghost" type="button" data-action="close-modal">关闭</button>'), true);
  } catch (error) { toast(error.message, "error"); }
}

export async function historyFullLog(id, attemptKey, target) {
  try {
    const query = `/api/history/detail?id=${encodeURIComponent(id)}&full=true&attempt=${encodeURIComponent(attemptKey)}`;
    const data = await api("GET", query);
    const info = (data.attemptLogs || []).find(log => String(log.number) === String(attemptKey));
    if (!info || info.logText == null) throw new Error("完整日志不存在或已被清理");
    const root = target.closest("[data-history-log]");
    const body = root?.querySelector("[data-history-log-body]");
    const meta = root?.querySelector("[data-history-log-meta]");
    if (!body || !meta) return;
    body.textContent = info.logText || "（无脚本日志）";
    meta.textContent = `脚本日志（第 ${attemptKey} 次尝试），${info.logTotalLines || 0} 行`;
    target.remove();
  } catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "history-detail": target => historyDetail(target.dataset.id),
  "history-days": target => historyDaysChange(target),
  "history-date": target => historySelectDate(target),
  "history-refresh": () => historyRefresh(),
  "history-full-log": target => withBusy(target, () => historyFullLog(target.dataset.id, target.dataset.attempt, target)),
};
