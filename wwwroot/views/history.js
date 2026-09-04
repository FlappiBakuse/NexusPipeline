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
let historyExpandedDates = new Set();
let historyUsersByDate = new Map();
let historySelectedUserKey = "";
let historySelectedUserName = "";
let historyRecords = [];
let historyDir = "";
let historyRangeGlobalBound = false;

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

function isHistoryMobile() {
  return window.matchMedia?.("(max-width: 820px)")?.matches ?? window.innerWidth <= 820;
}

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
  if (status === "skipped") return '<span class="badge blue">已跳过</span>';
  const reason = record.resultDetail ? `：${record.resultDetail}` : "";
  return `<span class="badge bad" title="${esc(reason)}">✕ 失败${esc(reason)}</span>`;
}

function historyRangeMarkup() {
  const displayValue = `${historyStartDate.replaceAll("-", "/")} 至 ${historyEndDate.replaceAll("-", "/")}`;
  return `<div class="history-range-search" data-history-range data-testid="history-range-search">
    <div class="history-range-picker">
      <button id="history-range-display" class="history-range-display" type="button" aria-haspopup="dialog" aria-expanded="false" aria-controls="history-range-popover" data-history-range-display data-testid="history-range-display"><span data-history-range-label>${esc(displayValue)}</span>
        <span class="history-range-icon" aria-hidden="true">${icon("calendar")}</span>
      </button>
      <div id="history-range-popover" class="history-range-popover secondary-surface" role="dialog" aria-label="选择时间段" hidden data-history-range-popover>
        <div class="history-calendar-toolbar"><button class="ghost sm" type="button" data-history-calendar-prev aria-label="上一个月份">‹</button><strong data-history-calendar-title>选择日期</strong><button class="ghost sm" type="button" data-history-calendar-next aria-label="下一个月份">›</button></div>
        <div class="history-calendar-months" data-history-calendar-months></div>
        <div class="history-range-selection"><span class="history-range-selection-item"><span class="muted">开始</span><strong data-history-range-from-label>${esc(historyStartDate.replaceAll("-", "/"))}</strong></span><span class="history-range-selection-arrow" aria-hidden="true">→</span><span class="history-range-selection-item"><span class="muted">结束</span><strong data-history-range-to-label>${esc(historyEndDate.replaceAll("-", "/"))}</strong></span></div>
        <div class="history-range-popover-footer"><span class="muted history-range-hint">点击日期选择范围，今天及之后的日期不可选</span><button class="primary sm" type="button" data-history-range-apply>应用范围</button></div>
      </div>
      <input id="history-from" type="hidden" value="${esc(historyStartDate)}" aria-label="开始日期" data-testid="history-from">
      <input id="history-to" type="hidden" value="${esc(historyEndDate)}" aria-label="结束日期" data-testid="history-to">
    </div>
  </div>`;
}

function monthKey(value) {
  const [year, month] = String(value || "").split("-").map(Number);
  const date = Number.isFinite(year) && Number.isFinite(month) ? new Date(year, month - 1, 1) : new Date();
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}`;
}

function shiftMonth(value, offset) {
  const [year, month] = monthKey(value).split("-").map(Number);
  const date = new Date(year, month - 1 + offset, 1);
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}`;
}

function monthLabel(value) {
  const [year, month] = monthKey(value).split("-");
  return `${year}年${month}月`;
}

function calendarMonthMarkup(value, start, end, maxDate) {
  const [year, month] = monthKey(value).split("-").map(Number);
  const first = new Date(year, month - 1, 1);
  const leading = first.getDay();
  const dayCount = new Date(year, month, 0).getDate();
  const weekdayLabels = ["日", "一", "二", "三", "四", "五", "六"];
  const cells = weekdayLabels.map(day => `<span class="history-calendar-weekday">${day}</span>`);
  for (let index = 0; index < leading; index++) cells.push('<span class="history-calendar-day is-empty" aria-hidden="true"></span>');
  for (let day = 1; day <= dayCount; day++) {
    const date = `${year}-${pad(month)}-${pad(day)}`;
    const isStart = date === start;
    const isEnd = date === end;
    const inRange = !!start && !!end && date > start && date < end;
    const disabled = date > maxDate;
    const classes = ["history-calendar-day", isStart ? "is-start" : "", isEnd ? "is-end" : "", inRange ? "is-in-range" : ""].filter(Boolean).join(" ");
    cells.push(`<button class="${classes}" type="button" data-history-calendar-day="${date}" aria-label="${fmtDateCN(date)}"${disabled ? " disabled" : ""}>${day}</button>`);
  }
  return `<section class="history-calendar-month" data-history-calendar-month="${monthKey(value)}"><h4>${monthLabel(value)}</h4><div class="history-calendar-grid">${cells.join("")}</div></section>`;
}

function syncHistoryRangeLabels(root) {
  const from = root.querySelector("#history-from")?.value || "";
  const to = root.querySelector("#history-to")?.value || "";
  const display = root.querySelector("[data-history-range-label]");
  const fromLabel = root.querySelector("[data-history-range-from-label]");
  const toLabel = root.querySelector("[data-history-range-to-label]");
  if (display) display.textContent = from && to ? `${from.replaceAll("-", "/")} 至 ${to.replaceAll("-", "/")}` : (from ? `${from.replaceAll("-", "/")} 至 选择结束日期` : "选择时间段");
  if (fromLabel) fromLabel.textContent = from ? from.replaceAll("-", "/") : "未选择";
  if (toLabel) toLabel.textContent = to ? to.replaceAll("-", "/") : "未选择";
}

function renderHistoryCalendar(root) {
  const months = root.querySelector("[data-history-calendar-months]");
  const title = root.querySelector("[data-history-calendar-title]");
  const previous = root.querySelector("[data-history-calendar-prev]");
  const next = root.querySelector("[data-history-calendar-next]");
  if (!months || !title) return;
  const maxDate = localDateIso();
  const count = window.innerWidth <= 560 ? 1 : 2;
  const anchor = monthKey(root.dataset.historyCalendarMonth || historyEndDate);
  const start = root.querySelector("#history-from")?.value || "";
  const end = root.querySelector("#history-to")?.value || "";
  months.innerHTML = Array.from({ length: count }, (_, index) => calendarMonthMarkup(shiftMonth(anchor, index), start, end, maxDate)).join("");
  title.textContent = count === 1 ? monthLabel(anchor) : `${monthLabel(anchor)} — ${monthLabel(shiftMonth(anchor, 1))}`;
  if (previous) previous.disabled = false;
  if (next) next.disabled = shiftMonth(anchor, count) > monthKey(maxDate);
  syncHistoryRangeLabels(root);
}

function setHistoryRangePickerOpen(root, open) {
  const display = root.querySelector("[data-history-range-display]");
  const popover = root.querySelector("[data-history-range-popover]");
  if (!display || !popover) return;
  popover.hidden = !open;
  display.setAttribute("aria-expanded", open ? "true" : "false");
}

function applyHistoryRangeFromPicker(root) {
  const from = root.querySelector("#history-from")?.value || "";
  const to = root.querySelector("#history-to")?.value || "";
  setHistoryRangePickerOpen(root, false);
  if (from && to) historyRangeSearch(root);
}

function bindHistoryRangePicker() {
  const root = document.querySelector("[data-history-range]");
  if (!root || root.dataset.bound === "true") return;
  const display = root.querySelector("[data-history-range-display]");
  if (!display) return;
  root.dataset.bound = "true";
  const open = () => setHistoryRangePickerOpen(root, true);
  display.addEventListener("click", open);
  if (!historyRangeGlobalBound) {
    document.addEventListener("pointerdown", event => {
      const currentRoot = document.querySelector("[data-history-range]");
      const currentPopover = currentRoot?.querySelector("[data-history-range-popover]");
      const target = event.target instanceof Element ? event.target : null;
      if (!currentRoot || !currentPopover || currentPopover.hidden || !target || currentRoot.contains(target)) return;
      applyHistoryRangeFromPicker(currentRoot);
    });
    document.addEventListener("keydown", event => {
      if (event.key !== "Escape") return;
      const currentRoot = document.querySelector("[data-history-range]");
      const currentPopover = currentRoot?.querySelector("[data-history-range-popover]");
      if (!currentRoot || !currentPopover || currentPopover.hidden) return;
      setHistoryRangePickerOpen(currentRoot, false);
      currentRoot.querySelector("[data-history-range-display]")?.focus();
    });
    historyRangeGlobalBound = true;
  }
  root.dataset.historyCalendarMonth = monthKey(historyStartDate);
  renderHistoryCalendar(root);
  root.addEventListener("click", event => {
    const target = event.target instanceof Element ? event.target : null;
    if (!target) return;
    const day = target.closest("[data-history-calendar-day]");
    if (day && !day.disabled) {
      const from = root.querySelector("#history-from");
      const to = root.querySelector("#history-to");
      if (!from || !to) return;
      const selected = day.dataset.historyCalendarDay || "";
      if (!from.value || to.value) {
        from.value = selected;
        to.value = "";
      } else if (selected < from.value) {
        to.value = from.value;
        from.value = selected;
      } else {
        to.value = selected;
      }
      renderHistoryCalendar(root);
      const nextFocus = from.value && to.value
        ? root.querySelector("[data-history-range-apply]")
        : root.querySelector(`[data-history-calendar-day="${selected}"]`);
      nextFocus?.focus();
      return;
    }
    const previous = target.closest("[data-history-calendar-prev]");
    if (previous && !previous.disabled) {
      root.dataset.historyCalendarMonth = shiftMonth(root.dataset.historyCalendarMonth, -1);
      renderHistoryCalendar(root);
      (root.querySelector("[data-history-calendar-prev]:not(:disabled)") || root.querySelector("[data-history-calendar-next]:not(:disabled)"))?.focus();
      return;
    }
    const next = target.closest("[data-history-calendar-next]");
    if (next && !next.disabled) {
      root.dataset.historyCalendarMonth = shiftMonth(root.dataset.historyCalendarMonth, 1);
      renderHistoryCalendar(root);
      (root.querySelector("[data-history-calendar-next]:not(:disabled)") || root.querySelector("[data-history-calendar-prev]:not(:disabled)"))?.focus();
      return;
    }
    if (target.closest("[data-history-range-apply]")) {
      applyHistoryRangeFromPicker(root);
    }
  });
  root.addEventListener("focusout", () => {
    window.setTimeout(() => {
      if (root.contains(document.activeElement)) return;
      setHistoryRangePickerOpen(root, false);
      const from = root.querySelector("#history-from")?.value || "";
      const to = root.querySelector("#history-to")?.value || "";
      if (from && to) historyRangeSearch(root);
    }, 0);
  });
}

function dateRowsMarkup() {
  return historyDates.length
    ? historyDates.map(date => {
      const expanded = historyExpandedDates.has(date.date);
      const users = historyUsersByDate.get(date.date);
      const usersMarkup = users === undefined
        ? '<div class="history-users-loading muted" role="status">正在加载运行用户…</div>'
        : userRowsMarkup(date.date, users);
      return `<div class="history-date-group${expanded ? " active" : ""}" data-history-date-group data-date="${esc(date.date)}" data-testid="history-date-group">
        <button class="history-date-row${expanded ? " active" : ""}" type="button" data-action="history-date" data-date="${esc(date.date)}" data-testid="history-date" aria-expanded="${expanded ? "true" : "false"}" aria-pressed="${expanded ? "true" : "false"}">${icon(expanded ? "chevronDown" : "chevronRight")}<span>${fmtDateCN(date.date)}</span><span class="muted">${date.count} 条</span></button>
        ${expanded ? `<div class="history-date-users" data-date="${esc(date.date)}" data-testid="history-date-users">${usersMarkup}</div>` : ""}
      </div>`;
    }).join("")
    : '<div class="history-dates-empty-message"><strong>该时间段暂无记录</strong><span>请选择其他日期范围。</span></div>';
}

function historyDetailBackMarkup() {
  return historySelectedUserKey
    ? '<button class="history-detail-back ghost" type="button" data-action="history-detail-back" data-testid="history-user-back">返回用户列表</button>'
    : "";
}

function userRowsMarkup(date, users = []) {
  if (!users.length) {
    return '<div class="history-empty-message"><strong>该日暂无运行用户</strong><span>请选择其他日期。</span></div>';
  }
  return users.map(user => {
    const name = user.userName || "未指定用户";
    const selected = date === historySelectedDate && user.userKey === historySelectedUserKey;
    return `<button class="history-user-row${selected ? " active" : ""}" type="button" data-action="history-user" data-history-date="${esc(date)}" data-user-key="${esc(user.userKey || "")}" data-user-name="${esc(name)}" data-testid="history-user" aria-pressed="${selected ? "true" : "false"}">
      <span class="history-user-avatar" aria-hidden="true">${icon("user")}</span>
      <span class="history-user-main"><strong>${esc(name)}</strong></span>
      <span class="history-user-arrow" aria-hidden="true">${icon("chevronRight")}</span>
    </button>`;
  }).join("");
}

function entryMarkup(record) {
  const queue = record.queueName ? ` · ${esc(record.queueName)}` : "";
  const pathParts = [historyDir, historySelectedDate, record.historyDirectory, record.logFile].filter(Boolean);
  const filePath = pathParts.length ? esc(pathParts.join("\\")) : "";
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

function panelsMarkup() {
  const hasDate = Boolean(historySelectedDate);
  const hasUser = Boolean(historySelectedUserKey);
  const usersVisible = isHistoryMobile() && hasDate && !hasUser;
  const detailVisible = isHistoryMobile() && hasUser;
  const modeClass = hasUser ? " history-user-selected" : usersVisible ? " history-users-visible" : "";
  const panelTitle = hasUser ? `${historySelectedUserName || "用户"} · 运行记录` : hasDate ? "运行记录" : "运行记录";
  const panelCount = hasUser ? `${historyRecords.length} 条记录` : "选择用户";
  const content = hasUser
    ? (historyRecords.length ? historyRecords.map(entryMarkup).join("") : '<div class="history-empty-message">该用户当天暂无运行记录</div>')
    : '<div class="history-empty-message"><strong>选择运行用户</strong><span>点击日期下的用户查看当天运行记录。</span></div>';
  return `<div class="history-browser${detailVisible ? " history-detail-visible" : ""}${modeClass}" data-testid="history-panels">
    <div class="history-list-column">
      ${historyRangeMarkup()}
      <aside class="history-dates-panel">
        <div class="history-panel-head">${icon("calendar")}<h3>日期列表</h3><span class="muted">${historyDates.length} 天</span></div>
        <div class="history-dates-list">${dateRowsMarkup()}</div>
      </aside>
    </div>
    <div class="history-records-column">
      ${hasDate ? historyDetailBackMarkup() : ""}
      <section class="history-records-panel history-level-panel">
        <div class="history-panel-head">${icon(hasUser ? "queues" : hasDate ? "scripts" : "history")}<h3>${esc(panelTitle)}</h3><span class="muted" data-testid="history-records-count">${esc(panelCount)}</span><button class="history-refresh" type="button" data-action="history-refresh" aria-label="刷新记录" data-testid="history-refresh">${icon("refresh")}</button></div>
        <div class="history-entry-list history-level-list">${content}</div>
      </section>
    </div>
  </div>`;
}

function historyRangeLabel() {
  return `${fmtDateCN(historyStartDate)} 至 ${fmtDateCN(historyEndDate)}`;
}

function historyViewLabel() {
  if (!historySelectedDate) return `${historyRangeLabel()} · 选择运行用户`;
  if (!historySelectedUserKey) return `${historyRangeLabel()} · ${fmtDateCN(historySelectedDate)} · 选择运行用户`;
  return `${historyRangeLabel()} · ${fmtDateCN(historySelectedDate)} · ${historySelectedUserName || "运行记录"}`;
}

function renderHistoryView() {
  render(pageHeader("历史记录", "历史记录", historyViewLabel(), "") + panelsMarkup());
  bindHistoryRangePicker();
}

export async function pageHistory(token) {
  if (!isCurrent("history", token)) return;
  navActive("history"); setTopbarTitle("历史记录");
  if (isHistoryMobile()) {
    historySelectedDate = "";
    historySelectedUserKey = "";
    historySelectedUserName = "";
  }
  let data;
  try {
    data = await api("GET", `/api/history/dates?from=${encodeURIComponent(historyStartDate)}&to=${encodeURIComponent(historyEndDate)}`);
  } catch (error) {
    if (isCurrent("history", token)) {
      historyDates = [];
      historySelectedDate = "";
      historyExpandedDates.clear();
      historyUsersByDate.clear();
      historySelectedUserKey = "";
      historySelectedUserName = "";
      historyRecords = [];
      toast(error.message, "error");
      renderHistoryView();
    }
    return;
  }
  if (!isCurrent("history", token)) return;
  historyDates = data.dates || [];
  const validDates = new Set(historyDates.map(date => date.date));
  if (isHistoryMobile()) {
    historyExpandedDates.clear();
    historyUsersByDate.clear();
    historySelectedDate = "";
    historySelectedUserKey = "";
    historySelectedUserName = "";
  } else {
    for (const date of historyExpandedDates) {
      if (!validDates.has(date)) {
        historyExpandedDates.delete(date);
        historyUsersByDate.delete(date);
      }
    }
    if (!historyExpandedDates.size && historyDates.length) historyExpandedDates.add(historyDates[0].date);
    if (!historyExpandedDates.has(historySelectedDate)) {
      historySelectedDate = historyDates.find(date => historyExpandedDates.has(date.date))?.date || "";
      historySelectedUserKey = "";
      historySelectedUserName = "";
      historyRecords = [];
      historyDir = "";
    }
  }
  if (!historySelectedDate) {
    historyRecords = [];
    historyDir = "";
    renderHistoryView();
    return;
  }
  await loadExpandedDayUsers(token);
}

/** 日期展开后只拉取该日期的用户聚合；点击用户后才拉取该用户的运行明细。 */
async function loadDayUsers(date, token, renderAfter = true) {
  if (!date || !historyExpandedDates.has(date)) return;
  let data;
  try {
    data = await api("GET", `/api/history/users?date=${encodeURIComponent(date)}`);
  } catch (error) {
    if (isCurrent("history", token) && historyExpandedDates.has(date)) {
      historyUsersByDate.set(date, []);
      toast(error.message, "error");
      if (renderAfter) renderHistoryView();
    }
    return;
  }
  if (!isCurrent("history", token) || !historyExpandedDates.has(date)) return;
  const users = data.users || [];
  historyUsersByDate.set(date, users);
  if (renderAfter) renderHistoryView();
}

async function loadExpandedDayUsers(token) {
  const dates = [...historyExpandedDates];
  await Promise.all(dates.map(date => loadDayUsers(date, token, false)));
  if (!isCurrent("history", token)) return;
  if (historySelectedDate && historySelectedUserKey) {
    await loadDayRecords(token);
    return;
  }
  renderHistoryView();
}

/** 用户选中后才拉取该用户当天的全部运行历史。 */
async function loadDayRecords(token) {
  if (!historySelectedDate || !historySelectedUserKey) return;
  let data;
  try {
    data = await api("GET", `/api/history?date=${encodeURIComponent(historySelectedDate)}&userKey=${encodeURIComponent(historySelectedUserKey)}`);
  } catch (error) {
    if (isCurrent("history", token)) {
      historyRecords = [];
      toast(error.message, "error");
      renderHistoryView();
    }
    return;
  }
  if (!isCurrent("history", token)) return;
  historyDir = data.historyDir || "";
  historyRecords = data.records || [];
  renderHistoryView();
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
  if (from === historyStartDate && to === historyEndDate) return;
  historyStartDate = from;
  historyEndDate = to;
  historySelectedDate = "";
  historyExpandedDates.clear();
  historyUsersByDate.clear();
  historySelectedUserKey = "";
  historySelectedUserName = "";
  historyRecords = [];
  historyDir = "";
  void pageHistory(state.routeToken);
}

/** 左侧日期行点击：独立切换日期展开状态并加载当天运行用户，同时保留当前已选运行记录。 */
export async function historySelectDate(target) {
  const date = target.dataset.date;
  if (!date || !historyDates.some(item => item.date === date)) return;
  const hasSelectedHistory = Boolean(historySelectedUserKey);
  if (historyExpandedDates.has(date)) {
    historyExpandedDates.delete(date);
    historyUsersByDate.delete(date);
    if (!hasSelectedHistory && historySelectedDate === date) {
      historySelectedDate = [...historyExpandedDates][0] || "";
    }
    renderHistoryView();
    return;
  }
  historyExpandedDates.add(date);
  if (!hasSelectedHistory) historySelectedDate = date;
  renderHistoryView();
  await loadDayUsers(date, state.routeToken);
}

/** 日期下的用户行点击：加载该用户当天的所有运行记录。 */
export async function historySelectUser(target) {
  const userKey = target.dataset.userKey || "";
  const date = target.dataset.historyDate || target.closest("[data-history-date-users]")?.dataset.date || "";
  if (!date || !historyExpandedDates.has(date) || !userKey) return;
  historySelectedDate = date;
  historySelectedUserKey = userKey;
  historySelectedUserName = target.dataset.userName || "未指定用户";
  await loadDayRecords(state.routeToken);
}

/** 右侧刷新按钮：按当前层级重新拉取用户或运行记录。 */
export async function historyRefresh() {
  if (!historySelectedDate) {
    await pageHistory(state.routeToken);
    return;
  }
  if (historySelectedUserKey) {
    await loadDayRecords(state.routeToken);
    return;
  }
  const expandedDates = [...historyExpandedDates];
  if (!expandedDates.length) {
    await pageHistory(state.routeToken);
    return;
  }
  await loadExpandedDayUsers(state.routeToken);
}

export function historyDetailBack() {
  if (historySelectedUserKey) {
    historySelectedUserKey = "";
    historySelectedUserName = "";
    historyRecords = [];
    historyDir = "";
    renderHistoryView();
  }
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

function historyImageUrl(id, attemptNumber, screenshotId) {
  return `/api/history/image?id=${encodeURIComponent(id)}&attempt=${encodeURIComponent(attemptNumber)}&screenshot=${encodeURIComponent(screenshotId)}`;
}

function historyAttemptScreenshotsMarkup(id, attempt, logInfo) {
  const screenshots = attempt.screenshots || logInfo?.screenshots || [];
  if (!screenshots.length) return "";
  const items = screenshots.map((screenshot, index) => {
    const imageUrl = screenshot.imageUrl || historyImageUrl(id, attempt.number, screenshot.id);
    const label = `第 ${attempt.number} 次尝试截图 ${index + 1}`;
    const details = [screenshot.width && screenshot.height ? `${screenshot.width}×${screenshot.height}` : "", screenshot.trigger || ""].filter(Boolean).join(" · ");
    return `<button class="history-screenshot-thumb" type="button" data-action="history-image" data-image-url="${esc(imageUrl)}" data-image-alt="${esc(label)}" data-image-caption="${esc(details)}" data-testid="history-screenshot"><img src="${esc(imageUrl)}" alt="${esc(label)}" loading="lazy"><span class="history-screenshot-index">${index + 1}</span></button>`;
  }).join("");
  return `<div class="history-attempt-screenshots" data-testid="history-attempt-screenshots"><div class="qk-row">运行截图（${screenshots.length} 张）</div><div class="history-screenshot-strip" role="list" aria-label="第 ${attempt.number} 次尝试运行截图">${items}</div></div>`;
}

function historyDetailMetaMarkup(record) {
  const user = record.userName || "未指定用户";
  const mode = record.mode === "auto" ? "自动运行" : "手动运行";
  return `<div class="history-detail-meta" data-testid="history-detail-meta">
    <div class="history-detail-meta-item"><span class="k">结果</span><span>${statusBadge(finalStatusOf(record))}</span></div>
    <div class="history-detail-meta-item"><span class="k">运行模式</span><span>${mode}</span></div>
    <div class="history-detail-meta-item"><span class="k">运行用户</span><span>${esc(user)}</span></div>
    <div class="history-detail-meta-item"><span class="k">尝试次数</span><span>${record.attempts || 0} / ${record.maxAttempts || "-"}</span></div>
    <div class="history-detail-meta-item"><span class="k">开始时间</span><span>${esc(fmtTime(record.startTime))}</span></div>
    <div class="history-detail-meta-item"><span class="k">结束时间</span><span>${esc(fmtTime(record.endTime))}</span></div>
    <div class="history-detail-meta-item history-detail-meta-wide"><span class="k">结果说明</span><span>${esc(record.resultDetail || "-")}</span></div>
  </div>`;
}

function historyImageLightboxMarkup() {
  return `<div class="history-image-lightbox" data-history-lightbox hidden role="dialog" aria-modal="true" aria-label="查看运行截图">
    <div class="history-image-lightbox-backdrop" data-action="history-image-close" aria-hidden="true"></div>
    <figure class="history-image-lightbox-content" data-history-lightbox-content><img data-history-lightbox-image alt=""><figcaption data-history-lightbox-caption></figcaption></figure>
    <button class="icon-button history-image-lightbox-close" type="button" data-action="history-image-close" aria-label="关闭截图预览">${icon("close")}</button>
  </div>`;
}

let historyLightboxOrigin = null;
let historyLightboxEscapeBound = false;
let historyLightboxParent = null;
let historyLightboxNextSibling = null;

function bindHistoryLightboxEscape() {
  if (historyLightboxEscapeBound) return;
  window.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    const lightbox = document.querySelector("[data-history-lightbox]");
    if (!lightbox || lightbox.hidden) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    historyCloseImage();
  }, true);
  historyLightboxEscapeBound = true;
}

export function historyOpenImage(target) {
  const lightbox = document.querySelector("[data-history-lightbox]");
  const image = lightbox?.querySelector("[data-history-lightbox-image]");
  if (!lightbox || !image || !target.dataset.imageUrl) return;
  if (lightbox.parentElement !== document.body) {
    historyLightboxParent = lightbox.parentNode;
    historyLightboxNextSibling = lightbox.nextSibling;
    document.body.appendChild(lightbox);
  }
  historyLightboxOrigin = target;
  image.src = target.dataset.imageUrl;
  image.alt = target.dataset.imageAlt || "运行截图";
  const caption = lightbox.querySelector("[data-history-lightbox-caption]");
  if (caption) caption.textContent = target.dataset.imageCaption || "";
  lightbox.hidden = false;
  lightbox.querySelector("[data-action=history-image-close]")?.focus();
}

export function historyCloseImage() {
  const lightbox = document.querySelector("[data-history-lightbox]");
  if (!lightbox || lightbox.hidden) return;
  lightbox.hidden = true;
  const image = lightbox.querySelector("[data-history-lightbox-image]");
  if (image) image.removeAttribute("src");
  const origin = historyLightboxOrigin;
  historyLightboxOrigin = null;
  const parent = historyLightboxParent;
  const nextSibling = historyLightboxNextSibling;
  historyLightboxParent = null;
  historyLightboxNextSibling = null;
  if (parent?.isConnected) parent.insertBefore(lightbox, nextSibling?.isConnected ? nextSibling : null);
  else lightbox.remove();
  if (origin && document.contains(origin)) origin.focus();
}

export async function historyDetail(id) {
  try {
    const data = await api("GET", "/api/history/detail?id=" + encodeURIComponent(id));
    const record = data.record;
    if (!record) return;
    const attempts = (record.attemptDetails || []).map(attempt => {
      const logInfo = (data.attemptLogs || []).find(l => l.number === attempt.number);
      const status = attempt.status === "success" ? "成功" : attempt.status === "partial" ? "部分完成" : attempt.status === "cancelled" ? "已取消" : attempt.status === "skipped" ? "已跳过" : "失败";
      const badgeClass = attempt.status === "success" ? "ok" : attempt.status === "partial" || attempt.status === "cancelled" ? "warn" : attempt.status === "skipped" ? "blue" : "bad";
      return `<section class="subsection history-attempt-detail"><div class="section-heading"><h3>第 ${attempt.number} 次尝试</h3><span class="badge ${badgeClass}">${status}</span></div><div class="history-attempt-meta"><div><span class="k">时间</span><span>${esc(fmtTime(attempt.startTime))} - ${esc(fmtTime(attempt.endTime))}</span></div><div><span class="k">原因</span><span>${esc(attempt.reason || "-")}</span></div></div>${historyLogMarkup(id, String(attempt.number), logInfo, `脚本日志（第 ${attempt.number} 次尝试）`)}${historyAttemptScreenshotsMarkup(id, attempt, logInfo)}</section>`;
    }).join("");
    const body = `${historyDetailMetaMarkup(record)}${pluginHistoryDetailMarkup(record)}${pluginSlotMarkup("history.detail.sections", "history.detail.sections", "history-detail-plugin-slot", { mode: "detail", primaryId: record.id })}<div class="history-attempt-list">${attempts}</div>${historyImageLightboxMarkup()}`;
    showModal(modalShell(`${esc(record.scriptName)} 运行详情`, body, '<button class="ghost" type="button" data-action="close-modal">关闭</button>'), true);
    bindHistoryLightboxEscape();
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
  "history-user": target => historySelectUser(target),
  "history-detail-back": () => historyDetailBack(),
  "history-refresh": () => historyRefresh(),
  "history-full-log": target => withBusy(target, () => historyFullLog(target.dataset.id, target.dataset.attempt, target)),
  "history-image": target => historyOpenImage(target),
  "history-image-close": () => historyCloseImage(),
};
