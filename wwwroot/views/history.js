import { api, apiBlob } from "../core/api.js";
import { esc, fmtTime, resultDetail, statusBadge } from "../core/format.js";
import { pageHeader } from "../core/forms.js";
import { icon } from "../core/icons.js";
import { isCurrent, state } from "../core/state.js";
import { modalShell, registerModalCleanup, showModal } from "../core/modal.js";
import { navActive, render, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { pluginSlotMarkup, renderPluginSlots } from "../core/plugin-slots.js";
import { applyTranslations, getLocale, t } from "../core/i18n.js";

let historyDates = [];
let historySelectedDate = "";
let historyExpandedDates = new Set();
let historyUsersByDate = new Map();
let historySelectedUserKey = "";
let historySelectedUserName = "";
let historyRecords = [];
let historyDir = "";
let historyRangeGlobalBound = false;
let historyImageSession = null;
let historyLightboxRequestToken = null;

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
  if (parts.length !== 3) return esc(dateStr);
  const date = new Date(Number(parts[0]), Number(parts[1]) - 1, Number(parts[2]));
  return Number.isNaN(date.getTime()) ? esc(dateStr) : date.toLocaleDateString(getLocale(), { year: "numeric", month: "short", day: "numeric" });
}

/** 「2026年08月21日 04:05:00」样式的完整时间文本。 */
function fmtDateTimeCN(value) {
  const d = new Date(value);
  if (!value || isNaN(d.getTime())) return "-";
  return d.toLocaleString(getLocale(), { dateStyle: "medium", timeStyle: "medium" });
}

/** 记录条状态徽章（参考图2）：成功=✓ 完成、失败=✕ 失败：原因、部分完成/已取消=警示色。 */
function entryBadge(record) {
  const status = record.status;
  if (status === "success") return `<span class="badge ok">✓ ${t("common.complete")}</span>`;
  if (status === "partial") return `<span class="badge warn">⚠ ${t("history.partially_complete")}</span>`;
  if (status === "cancelled") return `<span class="badge warn">${t("common.cancelled")}</span>`;
  if (status === "skipped") return `<span class="badge blue">${t("common.skipped")}</span>`;
  const detail = resultDetail(record);
  const reason = detail && detail !== "-" ? t("history.failure_reason", { reason: detail }) : t("common.failed");
  return `<span class="badge bad" title="${esc(reason)}">✕ ${esc(reason)}</span>`;
}

function historyRangeMarkup() {
  const displayValue = `${historyStartDate.replaceAll("-", "/")} ${t("common.to")} ${historyEndDate.replaceAll("-", "/")}`;
  return `<div class="history-range-search" data-history-range data-testid="history-range-search">
    <div class="history-range-picker">
      <button id="history-range-display" class="history-range-display" type="button" aria-haspopup="dialog" aria-expanded="false" aria-controls="history-range-popover" data-history-range-display data-testid="history-range-display"><span data-history-range-label>${esc(displayValue)}</span>
        <span class="history-range-icon" aria-hidden="true">${icon("calendar")}</span>
      </button>
      <div id="history-range-popover" class="history-range-popover secondary-surface" role="dialog" aria-label="${t("history.choose_time_range")}" hidden data-history-range-popover>
        <div class="history-calendar-toolbar"><button class="ghost sm" type="button" data-history-calendar-prev aria-label="${t("history.previous_month")}">‹</button><strong data-history-calendar-title>${t("history.choose_date")}</strong><button class="ghost sm" type="button" data-history-calendar-next aria-label="${t("history.next_month")}">›</button></div>
        <div class="history-calendar-months" data-history-calendar-months></div>
        <div class="history-range-selection"><span class="history-range-selection-item"><span class="muted">${t("common.start")}</span><strong data-history-range-from-label>${esc(historyStartDate.replaceAll("-", "/"))}</strong></span><span class="history-range-selection-arrow" aria-hidden="true">→</span><span class="history-range-selection-item"><span class="muted">${t("common.end")}</span><strong data-history-range-to-label>${esc(historyEndDate.replaceAll("-", "/"))}</strong></span></div>
        <div class="history-range-popover-footer"><span class="muted history-range-hint">${t("history.filter.date_help")}</span><button class="primary sm" type="button" data-history-range-apply>${t("history.apply_range")}</button></div>
      </div>
      <input id="history-from" type="hidden" value="${esc(historyStartDate)}" aria-label="${t("common.start")} ${t("common.date")}" data-testid="history-from">
      <input id="history-to" type="hidden" value="${esc(historyEndDate)}" aria-label="${t("common.end")} ${t("common.date")}" data-testid="history-to">
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
  return new Date(Number(year), Number(month) - 1, 1).toLocaleDateString(getLocale(), { year: "numeric", month: "long" });
}

function calendarMonthMarkup(value, start, end, maxDate) {
  const [year, month] = monthKey(value).split("-").map(Number);
  const first = new Date(year, month - 1, 1);
  const leading = first.getDay();
  const dayCount = new Date(year, month, 0).getDate();
  const weekdayLabels = [t("common.sun"), t("common.mon"), t("common.tue"), t("common.wed"), t("common.thu"), t("common.fri"), t("common.sat")];
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
  if (display) display.textContent = from && to ? `${from.replaceAll("-", "/")} ${t("common.to")} ${to.replaceAll("-", "/")}` : (from ? `${from.replaceAll("-", "/")} ${t("common.to")} ${t("history.choose_end_date")}` : t("history.choose_time_range"));
  if (fromLabel) fromLabel.textContent = from ? from.replaceAll("-", "/") : t("history.not_selected");
  if (toLabel) toLabel.textContent = to ? to.replaceAll("-", "/") : t("history.not_selected");
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
  applyTranslations(root);
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
        ? `<div class="history-users-loading muted" role="status">${t("history.loading_run_users")}</div>`
        : userRowsMarkup(date.date, users);
      return `<div class="history-date-group${expanded ? " active" : ""}" data-history-date-group data-date="${esc(date.date)}" data-testid="history-date-group">
        <button class="history-date-row${expanded ? " active" : ""}" type="button" data-action="history-date" data-date="${esc(date.date)}" data-testid="history-date" aria-expanded="${expanded ? "true" : "false"}" aria-pressed="${expanded ? "true" : "false"}">${icon(expanded ? "chevronDown" : "chevronRight")}<span>${fmtDateCN(date.date)}</span><span class="muted">${date.count} ${t("common.items")}</span></button>
        ${expanded ? `<div class="history-date-users" data-date="${esc(date.date)}" data-testid="history-date-users">${usersMarkup}</div>` : ""}
      </div>`;
    }).join("")
    : `<div class="history-dates-empty-message"><strong>${t("history.records.empty_range")}</strong><span>${t("history.filter.date_range_retry")}</span></div>`;
}

function historyDetailBackMarkup() {
  return historySelectedUserKey
    ? `<button class="history-detail-back ghost" type="button" data-action="history-detail-back" data-testid="history-user-back">${t("history.back_to_user_list")}</button>`
    : "";
}

function userRowsMarkup(date, users = []) {
  if (!users.length) {
    return `<div class="history-empty-message"><strong>${t("history.filter.no_users_day")}</strong><span>${t("history.choose_another_date")}</span></div>`;
  }
  return users.map(user => {
    const name = user.userName || t("history.no_user_specified");
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
  return `<button class="history-entry history-status-${esc(record.status)}" type="button" data-action="history-detail" data-id="${esc(record.id)}" data-testid="history-entry">
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
    return `<section class="subsection plugin-history-detail"><div class="section-heading"><h3>${esc(item.title || item.id || t("history.plugin_information"))}</h3><span class="muted">${esc(item.pluginDisplayName || item.pluginName || "")}</span></div>${badges ? `<div class="plugin-contribution-badge">${badges}</div>` : ""}${fields ? `<div class="detail">${fields}</div>` : ""}</section>`;
  }).join("");
  return items ? `<section class="plugin-history-section"><div class="section-heading"><h3>${t("history.detail.plugin_info")}</h3><span class="muted">${t("history.screenshot.snapshot_saved")}</span></div>${items}</section>` : "";
}

function panelsMarkup() {
  const hasDate = Boolean(historySelectedDate);
  const hasUser = Boolean(historySelectedUserKey);
  const usersVisible = isHistoryMobile() && hasDate && !hasUser;
  const detailVisible = isHistoryMobile() && hasUser;
  const modeClass = hasUser ? " history-user-selected" : usersVisible ? " history-users-visible" : "";
  const panelTitle = hasUser ? `${historySelectedUserName || t("common.user")} · ${t("history.run_records")}` : t("history.run_records");
  const panelCount = hasUser ? t("history.records.count", { count: historyRecords.length }) : t("history.choose_user");
  const content = hasUser
    ? (historyRecords.length ? historyRecords.map(entryMarkup).join("") : `<div class="history-empty-message">${t("history.records.empty_day")}</div>`)
    : `<div class="history-empty-message"><strong>${t("history.choose_run_users")}</strong><span>${t("history.filter.user_day_help")}</span></div>`;
  return `<div class="history-browser${detailVisible ? " history-detail-visible" : ""}${modeClass}" data-testid="history-panels">
    <div class="history-list-column">
      ${historyRangeMarkup()}
      <aside class="history-dates-panel">
        <div class="history-panel-head">${icon("calendar")}<h3>${t("history.date_list")}</h3><span class="muted">${historyDates.length} ${t("history.days")}</span></div>
        <div class="history-dates-list">${dateRowsMarkup()}</div>
      </aside>
    </div>
    <div class="history-records-column">
      ${hasDate ? historyDetailBackMarkup() : ""}
      <section class="history-records-panel history-level-panel">
        <div class="history-panel-head">${icon(hasUser ? "queues" : hasDate ? "scripts" : "history")}<h3>${esc(panelTitle)}</h3><span class="muted" data-testid="history-records-count">${esc(panelCount)}</span><button class="history-refresh" type="button" data-action="history-refresh" aria-label="${t("history.refresh_records")}" data-testid="history-refresh">${icon("refresh")}</button></div>
        <div class="history-entry-list history-level-list">${content}</div>
      </section>
    </div>
  </div>`;
}

function historyRangeLabel() {
  return `${fmtDateCN(historyStartDate)} ${t("common.to")} ${fmtDateCN(historyEndDate)}`;
}

function historyViewLabel() {
  if (!historySelectedDate) return `${historyRangeLabel()} · ${t("history.choose_run_users")}`;
  if (!historySelectedUserKey) return `${historyRangeLabel()} · ${fmtDateCN(historySelectedDate)} · ${t("history.choose_run_users")}`;
  return `${historyRangeLabel()} · ${fmtDateCN(historySelectedDate)} · ${historySelectedUserName || t("history.run_records")}`;
}

function renderHistoryView() {
  render(pageHeader(t("shell.history"), t("shell.history"), historyViewLabel(), "") + panelsMarkup());
  bindHistoryRangePicker();
}

export async function pageHistory(token) {
  if (!isCurrent("history", token)) return;
  navActive("history"); setTopbarTitle(t("shell.history"));
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
    toast(t("history.filter.date_range"), "error");
    return;
  }
  if (from > to) {
    toast(t("history.filter.invalid_range"), "error");
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
  historySelectedUserName = target.dataset.userName || t("history.no_user_specified");
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
  const hasTail = total > 200 && !full;
  const action = total > 200 && !full
    ? `<div class="history-log-actions"><span class="muted">${t("history.log.tail_only")}</span><button class="ghost sm" type="button" data-action="history-full-log" data-id="${esc(id)}" data-attempt="${esc(attemptKey)}">${t("history.view_full_log")}</button></div>`
    : "";
  const logText = full ? logInfo.logText : (logInfo?.logTail || t("history.no_script_log"));
  const lineSummary = logInfo
    ? t(hasTail ? "history.log.lines_summary.tail" : "history.log.lines_summary", { label, count: total, lines: t("history.lines") })
    : "";
  return `<div class="history-log" data-history-log data-attempt="${esc(attemptKey)}"><div class="qk-row" data-history-log-meta>${esc(logInfo ? lineSummary : label)}</div>${action}<pre class="logbox" data-history-log-body>${esc(logText)}</pre></div>`;
}

function historyImageUrl(id, attemptNumber, screenshotId) {
  return `/api/history/image?id=${encodeURIComponent(id)}&attempt=${encodeURIComponent(attemptNumber)}&screenshot=${encodeURIComponent(screenshotId)}`;
}

function historyAttemptScreenshotsMarkup(id, attempt, logInfo) {
  const screenshots = attempt.screenshots || logInfo?.screenshots || [];
  if (!screenshots.length) return "";
  const items = screenshots.map((screenshot, index) => {
    const imageUrl = screenshot.imageUrl || historyImageUrl(id, attempt.number, screenshot.id);
    const label = t("history.screenshot.item", { attempt: attempt.number, index: index + 1 });
    const details = [screenshot.width && screenshot.height ? `${screenshot.width}×${screenshot.height}` : "", screenshot.trigger || ""].filter(Boolean).join(" · ");
    return `<button class="history-screenshot-thumb" type="button" data-action="history-image" data-image-url="${esc(imageUrl)}" data-image-alt="${esc(label)}" data-image-caption="${esc(details)}" data-testid="history-screenshot"><img alt="${esc(label)}" loading="lazy" data-history-image><span class="history-screenshot-index">${index + 1}</span></button>`;
  }).join("");
  return `<div class="history-attempt-screenshots" data-testid="history-attempt-screenshots"><div class="qk-row">${t("history.screenshots.summary", { count: screenshots.length })}</div><div class="history-screenshot-strip" role="list" aria-label="${t("history.screenshot.attempt_summary", { attempt: attempt.number })}">${items}</div></div>`;
}

function historyDetailMetaMarkup(record) {
  const user = record.userName || t("history.no_user_specified");
  const mode = record.mode === "auto" ? t("common.automatic_run") : t("history.run_manually");
  return `<div class="history-detail-meta" data-testid="history-detail-meta">
    <div class="history-detail-meta-item"><span class="k">${t("history.result")}</span><span>${statusBadge(record.status)}</span></div>
    <div class="history-detail-meta-item"><span class="k">${t("history.run_mode")}</span><span>${mode}</span></div>
    <div class="history-detail-meta-item"><span class="k">${t("history.run_users")}</span><span>${esc(user)}</span></div>
    <div class="history-detail-meta-item"><span class="k">${t("history.attempts")}</span><span>${record.attempts || 0} / ${record.maxAttempts || "-"}</span></div>
    <div class="history-detail-meta-item"><span class="k">${t("history.start_time")}</span><span>${esc(fmtTime(record.startTime))}</span></div>
    <div class="history-detail-meta-item"><span class="k">${t("history.end_time")}</span><span>${esc(fmtTime(record.endTime))}</span></div>
    <div class="history-detail-meta-item history-detail-meta-wide"><span class="k">${t("history.result_description")}</span><span>${esc(resultDetail(record))}</span></div>
  </div>`;
}

function historyImageLightboxMarkup() {
  return `<div class="history-image-lightbox" data-history-lightbox hidden role="dialog" aria-modal="true" aria-label="${t("history.view_run_screenshot")}">
    <div class="history-image-lightbox-backdrop" data-action="history-image-close" aria-hidden="true"></div>
    <figure class="history-image-lightbox-content" data-history-lightbox-content><img data-history-lightbox-image alt=""><figcaption data-history-lightbox-caption></figcaption></figure>
    <button class="icon-button history-image-lightbox-close" type="button" data-action="history-image-close" aria-label="${t("history.screenshot.close")}">${icon("close")}</button>
  </div>`;
}

let historyLightboxOrigin = null;
let historyLightboxEscapeBound = false;
let historyLightboxParent = null;
let historyLightboxNextSibling = null;

function newHistoryImageSession() {
  const session = {
    controller: new AbortController(),
    requests: new Map(),
    urls: new Map(),
    closed: false,
  };
  historyImageSession = session;
  return session;
}

function disposeHistoryImageSession(session) {
  if (!session || session.closed) return;
  session.closed = true;
  session.controller.abort();
  for (const url of session.urls.values()) URL.revokeObjectURL(url);
  session.urls.clear();
  session.requests.clear();
  if (historyImageSession === session) historyImageSession = null;
}

async function loadHistoryImage(session, imageUrl) {
  if (!session || session.closed) throw new DOMException(t("history.image_session_closed"), "AbortError");
  const cached = session.urls.get(imageUrl);
  if (cached) return cached;
  const existing = session.requests.get(imageUrl);
  if (existing) return existing;
  const request = apiBlob(imageUrl, session.controller.signal)
    .then(blob => {
      if (!blob.type.startsWith("image/")) throw new Error(t("history.screenshot.invalid_format"));
      const url = URL.createObjectURL(blob);
      if (session.closed) {
        URL.revokeObjectURL(url);
        throw new DOMException(t("history.image_session_closed"), "AbortError");
      }
      session.urls.set(imageUrl, url);
      return url;
    })
    .finally(() => session.requests.delete(imageUrl));
  session.requests.set(imageUrl, request);
  return request;
}

async function hydrateHistoryImages(session) {
  const elements = [...document.querySelectorAll("[data-history-image]")];
  await Promise.all(elements.map(async element => {
    const imageUrl = element.closest("[data-image-url]")?.dataset.imageUrl;
    if (!imageUrl) return;
    try {
      const objectUrl = await loadHistoryImage(session, imageUrl);
      if (!session.closed && element.isConnected) element.src = objectUrl;
    } catch (error) {
      if (error?.name !== "AbortError" && element.isConnected) element.dataset.imageError = "1";
    }
  }));
}

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

export async function historyOpenImage(target) {
  const lightbox = document.querySelector("[data-history-lightbox]");
  const image = lightbox?.querySelector("[data-history-lightbox-image]");
  if (!lightbox || !image || !target.dataset.imageUrl) return;
  const session = historyImageSession;
  if (!session) return;
  if (lightbox.parentElement !== document.body) {
    historyLightboxParent = lightbox.parentNode;
    historyLightboxNextSibling = lightbox.nextSibling;
    document.body.appendChild(lightbox);
  }
  historyLightboxOrigin = target;
  const requestToken = Symbol("history-lightbox-request");
  historyLightboxRequestToken = requestToken;
  image.removeAttribute("src");
  image.alt = target.dataset.imageAlt || t("history.run_screenshot");
  const caption = lightbox.querySelector("[data-history-lightbox-caption]");
  if (caption) caption.textContent = target.dataset.imageCaption || "";
  lightbox.hidden = false;
  lightbox.querySelector("[data-action=history-image-close]")?.focus();
  try {
    const objectUrl = await loadHistoryImage(session, target.dataset.imageUrl);
    if (historyLightboxRequestToken === requestToken && !session.closed && image.isConnected) image.src = objectUrl;
  } catch (error) {
    if (error?.name !== "AbortError" && historyLightboxRequestToken === requestToken) toast(error.message, "error");
  }
}

export function historyCloseImage() {
  const lightbox = document.querySelector("[data-history-lightbox]");
  if (!lightbox || lightbox.hidden) return;
  lightbox.hidden = true;
  historyLightboxRequestToken = null;
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
      const status = attempt.status === "success" ? t("common.success") : attempt.status === "partial" ? t("history.partially_complete") : attempt.status === "cancelled" ? t("common.cancelled") : attempt.status === "skipped" ? t("common.skipped") : t("common.failed");
      const badgeClass = attempt.status === "success" ? "ok" : attempt.status === "partial" || attempt.status === "cancelled" ? "warn" : attempt.status === "skipped" ? "blue" : "bad";
      const attemptReason = attempt.reason || "-";
      return `<section class="subsection history-attempt-detail"><div class="section-heading"><h3>${t("common.run.attempt", { attempt: attempt.number })}</h3><span class="badge ${badgeClass}">${status}</span></div><div class="history-attempt-meta"><div><span class="k">${t("common.time")}</span><span>${esc(fmtTime(attempt.startTime))} - ${esc(fmtTime(attempt.endTime))}</span></div><div><span class="k">${t("common.reason")}</span><span>${esc(attemptReason)}</span></div></div>${historyLogMarkup(id, String(attempt.number), logInfo, t("history.log.attempt", { attempt: attempt.number }))}${historyAttemptScreenshotsMarkup(id, attempt, logInfo)}</section>`;
    }).join("");
    const body = `${historyDetailMetaMarkup(record)}${pluginHistoryDetailMarkup(record)}${pluginSlotMarkup("history.detail.sections", "history.detail.sections", "history-detail-plugin-slot", { mode: "detail", primaryId: record.id })}<div class="history-attempt-list">${attempts}</div>${historyImageLightboxMarkup()}`;
    showModal(modalShell(`${esc(record.scriptName)} ${t("history.run_details")}`, body, `<button class="ghost" type="button" data-action="close-modal">${t("common.close")}</button>`), true);
    const imageSession = newHistoryImageSession();
    registerModalCleanup(() => {
      historyCloseImage();
      disposeHistoryImageSession(imageSession);
    });
    bindHistoryLightboxEscape();
    void hydrateHistoryImages(imageSession);
    void renderPluginSlots(document);
  } catch (error) { toast(error.message, "error"); }
}

export async function historyFullLog(id, attemptKey, target) {
  try {
    const query = `/api/history/detail?id=${encodeURIComponent(id)}&full=true&attempt=${encodeURIComponent(attemptKey)}`;
    const data = await api("GET", query);
    const info = (data.attemptLogs || []).find(log => String(log.number) === String(attemptKey));
    if (!info || info.logText == null) throw new Error(t("history.log.missing"));
    const root = target.closest("[data-history-log]");
    const body = root?.querySelector("[data-history-log-body]");
    const meta = root?.querySelector("[data-history-log-meta]");
    if (!body || !meta) return;
    body.textContent = info.logText || t("history.no_script_log");
  meta.textContent = t("history.log.lines_summary", { label: t("history.log.attempt", { attempt: attemptKey }), count: info.logTotalLines || 0, lines: t("history.lines") });
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
