import { api } from "../core/api.js";
import { esc, finalStatusOf, fmtTime, statusBadge } from "../core/format.js";
import { pageHeader } from "../core/forms.js";
import { icon } from "../core/icons.js";
import { isCurrent, state } from "../core/state.js";
import { modalShell, showModal } from "../core/modal.js";
import { navActive, render, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { pluginSlotMarkup, renderPluginSlots } from "../core/plugin-slots.js";

let historyDates = [];
let historySelectedDate = "";
let historyDir = "";

const pad = n => String(n).padStart(2, "0");

function localDateIso(date = new Date()) {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function offsetDateIso(value, offset) {
  const [year, month, day] = String(value).split("-").map(Number);
  const date = new Date(year, month - 1, day);
  date.setDate(date.getDate() + offset);
  return localDateIso(date);
}

const historyToday = localDateIso();
let historyStartDate = offsetDateIso(historyToday, -29);
let historyEndDate = historyToday;

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

function historyRangeMarkup() {
  const maxDate = localDateIso();
  const displayValue = `${historyStartDate.replaceAll("-", "/")} 至 ${historyEndDate.replaceAll("-", "/")}`;
  return `<div class="history-range-search" data-history-range data-testid="history-range-search">
    <label class="field-label" for="history-range-display">时间段</label>
    <div class="history-range-picker">
      <input id="history-range-display" class="history-range-display" type="text" value="${esc(displayValue)}" placeholder="选择时间段" readonly aria-haspopup="dialog" aria-expanded="false" aria-controls="history-range-popover" data-history-range-display data-testid="history-range-display">
      <span class="history-range-icon" aria-hidden="true">${icon("calendar")}</span>
      <div id="history-range-popover" class="history-range-popover" role="dialog" aria-label="选择时间段" hidden data-history-range-popover>
        <div class="history-range-popover-fields">
          <label for="history-from">开始日期<input id="history-from" type="date" value="${esc(historyStartDate)}" max="${maxDate}" aria-label="开始日期" data-testid="history-from"></label>
          <label for="history-to">结束日期<input id="history-to" type="date" value="${esc(historyEndDate)}" max="${maxDate}" aria-label="结束日期" data-testid="history-to"></label>
        </div>
        <span class="muted history-range-hint">选择完成后失焦自动查询</span>
      </div>
    </div>
  </div>`;
}

function setHistoryRangePickerOpen(root, open) {
  const display = root.querySelector("[data-history-range-display]");
  const popover = root.querySelector("[data-history-range-popover]");
  if (!display || !popover) return;
  popover.hidden = !open;
  display.setAttribute("aria-expanded", open ? "true" : "false");
}

function bindHistoryRangePicker() {
  const root = document.querySelector("[data-history-range]");
  if (!root || root.dataset.bound === "true") return;
  const display = root.querySelector("[data-history-range-display]");
  if (!display) return;
  root.dataset.bound = "true";
  const open = () => setHistoryRangePickerOpen(root, true);
  display.addEventListener("focus", open);
  display.addEventListener("click", open);
  root.addEventListener("focusout", () => {
    window.setTimeout(() => {
      if (root.contains(document.activeElement)) return;
      setHistoryRangePickerOpen(root, false);
      historyRangeSearch(root);
    }, 0);
  });
}

function dateRowsMarkup() {
  return historyDates.length
    ? historyDates.map(date => `<button class="history-date-row${date.date === historySelectedDate ? " active" : ""}" type="button" data-action="history-date" data-date="${esc(date.date)}" data-testid="history-date" aria-pressed="${date.date === historySelectedDate ? "true" : "false"}">${icon("chevronRight")}<span>${fmtDateCN(date.date)}</span><span class="muted">${date.count} 条</span></button>`).join("")
    : '<div class="empty compact-empty"><strong>该时间段暂无记录</strong><span>请选择其他日期范围。</span></div>';
}

function entryMarkup(record) {
  const queue = record.queueName ? ` · ${esc(record.queueName)}` : "";
  const filePath = historyDir && historySelectedDate ? `${historyDir}\\${historySelectedDate}\\${esc(record.logFile || "")}` : esc(record.logFile || "");
  return `<button class="history-entry history-status-${esc(finalStatusOf(record))}" type="button" data-action="history-detail" data-id="${esc(record.id)}" data-testid="history-entry">
    <span class="history-entry-bar" aria-hidden="true"></span>
    <span class="history-entry-main">
      <span class="history-entry-title"><strong>${fmtDateTimeCN(record.startTime)} · ${esc(record.scriptName)}${queue}</strong>${entryBadge(record)}${pluginHistoryBadges(record)}${pluginSlotMarkup("history.list.badges", `history-${record.id}`, "history-plugin-slot", { mode: "list", primaryId: record.id })}</span>
      <span class="history-entry-path">${filePath}</span>
    </span>
    <span class="history-entry-arrow" aria-hidden="true">${icon("chevronRight")}</span>
  </button>`;
}

function pluginHistoryBadges(record) {
  const tones = new Set(["muted", "blue", "ok", "warn", "bad"]);
  return (record.pluginHistory || []).flatMap(item => (item.badges || []).map(badge => {
    const tone = tones.has(String(badge.tone || "").toLowerCase()) ? String(badge.tone).toLowerCase() : "muted";
    return `<span class="badge ${tone}" title="${esc(badge.title || item.pluginDisplayName || item.pluginName || "")}">${esc(badge.label || "")}</span>`;
  })).join("");
}

function pluginHistoryDetailMarkup(record) {
  const tones = new Set(["muted", "blue", "ok", "warn", "bad"]);
  const items = (record.pluginHistory || []).map(item => {
    const badges = (item.badges || []).map(badge => {
      const tone = tones.has(String(badge.tone || "").toLowerCase()) ? String(badge.tone).toLowerCase() : "muted";
      return `<span class="badge ${tone}" title="${esc(badge.title || "")}">${esc(badge.label || "")}</span>`;
    }).join("");
    const fields = (item.fields || []).map(field => `<div class="kv"><span class="k">${esc(field.label || "")}</span><span>${esc(field.value || "")}</span></div>`).join("");
    return `<section class="subsection plugin-history-detail"><div class="section-heading"><h3>${esc(item.title || item.id || "插件信息")}</h3><span class="muted">${esc(item.pluginDisplayName || item.pluginName || "")}</span></div>${badges ? `<div class="plugin-contribution-badge">${badges}</div>` : ""}${fields ? `<div class="detail">${fields}</div>` : ""}</section>`;
  }).join("");
  return items ? `<section class="plugin-history-section"><div class="section-heading"><h3>插件运行信息</h3><span class="muted">运行完成时保存的展示快照</span></div>${items}</section>` : "";
}

function panelsMarkup(records) {
  const emptyMessage = historySelectedDate ? "该日暂无记录" : "该时间段暂无记录";
  return `<div class="history-browser" data-testid="history-panels">
    <div class="history-list-column">
      ${historyRangeMarkup()}
      <aside class="history-dates-panel">
        <div class="history-panel-head">${icon("calendar")}<h3>运行日期</h3><span class="muted">${historyDates.length} 天</span></div>
        <div class="history-dates-list">${dateRowsMarkup()}</div>
      </aside>
    </div>
    <div class="history-records-column">
      <section class="history-records-panel">
        <div class="history-panel-head">${icon("queues")}<h3>运行记录</h3><span class="muted" data-testid="history-records-count">${records.length} 条记录</span><button class="history-refresh" type="button" data-action="history-refresh" aria-label="刷新记录" data-testid="history-refresh">${icon("refresh")}</button></div>
        <div class="history-entry-list">${records.length ? records.map(entryMarkup).join("") : `<div class="empty"><strong>${emptyMessage}</strong></div>`}</div>
      </section>
    </div>
  </div>`;
}

function historyRangeLabel() {
  return `${fmtDateCN(historyStartDate)} 至 ${fmtDateCN(historyEndDate)}`;
}

export async function pageHistory(token) {
  if (!isCurrent("history", token)) return;
  navActive("history"); setTopbarTitle("历史记录");
  let data;
  try {
    data = await api("GET", `/api/history/dates?from=${encodeURIComponent(historyStartDate)}&to=${encodeURIComponent(historyEndDate)}`);
  } catch (error) {
    if (isCurrent("history", token)) {
      historyDates = [];
      historySelectedDate = "";
      toast(error.message, "error");
      render(pageHeader("历史记录", "历史记录", historyRangeLabel(), "") + panelsMarkup([]));
      bindHistoryRangePicker();
    }
    return;
  }
  if (!isCurrent("history", token)) return;
  historyDates = data.dates || [];
  if (!historyDates.some(date => date.date === historySelectedDate)) {
    historySelectedDate = historyDates[0]?.date || "";
  }
  if (!historySelectedDate) {
    render(pageHeader("历史记录", "历史记录", `${historyRangeLabel()} · 暂无运行记录`, "") + panelsMarkup([]));
    bindHistoryRangePicker();
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
      render(pageHeader("历史记录", "历史记录", historyRangeLabel(), "") + panelsMarkup([]));
      bindHistoryRangePicker();
    }
    return;
  }
  if (!isCurrent("history", token)) return;
  historyDir = data.historyDir || "";
  const records = data.records || [];
  render(pageHeader("历史记录", "历史记录", `${historyRangeLabel()} · 按日期查看运行记录`, "") + panelsMarkup(records));
  bindHistoryRangePicker();
  await renderPluginSlots(document.querySelector("#view"));
}

/** 按年月日范围重新查询历史记录。 */
export function historyRangeSearch(root = document.querySelector("[data-history-range]")) {
  const scope = root || document;
  const from = scope.querySelector("#history-from")?.value || "";
  const to = scope.querySelector("#history-to")?.value || "";
  if (!from || !to) {
    toast("请选择开始日期和结束日期", "error");
    return;
  }
  if (from > to) {
    toast("开始日期不能晚于结束日期", "error");
    return;
  }
  historyStartDate = from;
  historyEndDate = to;
  historySelectedDate = "";
  void pageHistory(state.routeToken);
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
  if (!historySelectedDate) {
    await pageHistory(state.routeToken);
    return;
  }
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
    const body = `<div class="history-summary"><div><span class="k">结果</span><span class="v">${statusBadge(finalStatusOf(record))}</span></div><div><span class="k">运行模式</span><span class="v">${record.mode === "auto" ? "自动运行" : "手动运行"}</span></div><div><span class="k">尝试次数</span><span class="v">${record.attempts || 0} / ${record.maxAttempts || "-"}</span></div><div><span class="k">开始时间</span><span class="v">${esc(fmtTime(record.startTime))}</span></div></div><div class="detail"><div class="kv"><span class="k">开始</span><span>${esc(fmtTime(record.startTime))}</span></div><div class="kv"><span class="k">结束</span><span>${esc(fmtTime(record.endTime))}</span></div><div class="kv"><span class="k">模式</span><span>${record.mode === "auto" ? "自动运行" : "手动运行"}</span></div>${record.userName ? `<div class="kv"><span class="k">用户</span><span>${esc(record.userName)}</span></div>` : ""}<div class="kv"><span class="k">重试</span><span>${record.attempts || 0} / ${record.maxAttempts || "-"} 次</span></div><div class="kv"><span class="k">结果</span><span>${statusBadge(finalStatusOf(record))} ${esc(record.resultDetail)}</span></div></div>${pluginHistoryDetailMarkup(record)}${pluginSlotMarkup("history.detail.sections", "history.detail.sections", "history-detail-plugin-slot", { mode: "detail", primaryId: record.id })}${attempts}`;
    showModal(modalShell(`${esc(record.scriptName)} 运行详情`, body, '<button class="ghost" type="button" data-action="close-modal">关闭</button>'), true);
    void renderPluginSlots(document);
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
  "history-date": target => historySelectDate(target),
  "history-refresh": () => historyRefresh(),
  "history-full-log": target => withBusy(target, () => historyFullLog(target.dataset.id, target.dataset.attempt, target)),
};
