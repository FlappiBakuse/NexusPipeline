<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { api, isAbortError } from "../../platform/api";
import { getLocale, t } from "../../platform/i18n";
import { disposePluginSlot } from "@bridge/index";
import { renderPluginSlot } from "@bridge/index";
import { setTopbarTitle } from "../../platform/shell";
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";
import HistoryDetailModal from "./components/HistoryDetailModal.vue";
import { formatDateTime, historyBadges, statusLabel, statusTone } from "./utils/historyFormat";
import type { HistoryDate, HistoryRecord, HistoryUser } from "./utils/historyTypes";

const today = new Date();
const pad = (value: number) => String(value).padStart(2, "0");
const dateValue = (value: Date) => `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}`;
const start = new Date(today);
start.setDate(start.getDate() - 29);

const from = ref(dateValue(start));
const to = ref(dateValue(today));
const dates = ref<HistoryDate[]>([]);
const expanded = ref(new Set<string>());
const usersByDate = ref(new Map<string, HistoryUser[]>());
const selectedDate = ref("");
const selectedUserKey = ref("");
const selectedUserName = ref("");
const records = ref<HistoryRecord[]>([]);
const historyDir = ref("");
const loading = ref(true);
const error = ref("");
const detail = ref<HistoryRecord | null>(null);
const historyRoot = ref<HTMLElement | null>(null);
const mobile = ref(false);
const rangeOpen = ref(false);
const rangeDraftFrom = ref(from.value);
const rangeDraftTo = ref(to.value);
const rangeAnchor = ref<"from" | "to">("from");
const calendarMonth = ref(`${start.getFullYear()}-${pad(start.getMonth() + 1)}`);
let requestId = 0;
let resizeHandler: (() => void) | null = null;

const formatDate = (value: string) => {
  const parsed = new Date(`${value}T00:00:00`);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleDateString(getLocale(), { year: "numeric", month: "short", day: "numeric" });
};

const monthKey = (value: string) => {
  const [year, month] = String(value || "").split("-").map(Number);
  return Number.isFinite(year) && Number.isFinite(month) ? `${year}-${pad(month)}` : `${today.getFullYear()}-${pad(today.getMonth() + 1)}`;
};
const shiftMonth = (value: string, offset: number) => {
  const [year, month] = monthKey(value).split("-").map(Number);
  const date = new Date(year, month - 1 + offset, 1);
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}`;
};
const monthLabel = (value: string) => {
  const [year, month] = monthKey(value).split("-").map(Number);
  return new Date(year, month - 1, 1).toLocaleDateString(getLocale(), { year: "numeric", month: "long" });
};
const calendarMonths = computed(() => [calendarMonth.value, shiftMonth(calendarMonth.value, 1)]);
const calendarTitle = computed(() => `${monthLabel(calendarMonths.value[0])} — ${monthLabel(calendarMonths.value[1])}`);
const canNextMonth = computed(() => calendarMonths.value[1] < monthKey(dateValue(today)));
function calendarDays(value: string) {
  const [year, month] = monthKey(value).split("-").map(Number);
  const first = new Date(year, month - 1, 1);
  const count = new Date(year, month, 0).getDate();
  const days: Array<{ value: string; empty: boolean; inRange: boolean; start: boolean; end: boolean }> = [];
  for (let index = 0; index < first.getDay(); index += 1) days.push({ value: "", empty: true, inRange: false, start: false, end: false });
  for (let day = 1; day <= count; day += 1) {
    const date = `${year}-${pad(month)}-${pad(day)}`;
    days.push({ value: date, empty: false, inRange: Boolean(rangeDraftFrom.value && rangeDraftTo.value && date > rangeDraftFrom.value && date < rangeDraftTo.value), start: date === rangeDraftFrom.value, end: date === rangeDraftTo.value });
  }
  return days;
}
const rangeDisplay = computed(() => `${rangeDraftFrom.value.replaceAll("-", "/")} ${t("common.to")} ${rangeDraftTo.value.replaceAll("-", "/")}`);
function openRangePicker() {
  rangeDraftFrom.value = from.value;
  rangeDraftTo.value = to.value;
  rangeAnchor.value = "from";
  calendarMonth.value = monthKey(from.value);
  rangeOpen.value = true;
}
function chooseRangeDate(value: string) {
  if (!value || value > dateValue(today)) return;
  if (rangeAnchor.value === "from") {
    rangeDraftFrom.value = value;
    if (rangeDraftTo.value && rangeDraftTo.value < value) rangeDraftTo.value = value;
    rangeAnchor.value = "to";
    return;
  }
  if (value < rangeDraftFrom.value) {
    rangeDraftTo.value = rangeDraftFrom.value;
    rangeDraftFrom.value = value;
  } else {
    rangeDraftTo.value = value;
  }
  rangeAnchor.value = "from";
}
function moveCalendar(offset: number) {
  const next = shiftMonth(calendarMonth.value, offset);
  if (offset > 0 && !canNextMonth.value) return;
  calendarMonth.value = next;
}

const selectedUser = computed(() => selectedUserName.value || t("history.no_user_specified"));
const panelTitle = computed(() => selectedUserKey.value ? `${selectedUser.value} · ${t("history.run_records")}` : t("history.run_records"));
const panelCount = computed(() => selectedUserKey.value ? t("history.records.count", { count: records.value.length }) : t("history.choose_user"));

function updateMobile() {
  mobile.value = window.innerWidth <= 820;
}

function resetSelectionForMobile() {
  if (!mobile.value || selectedUserKey.value) return;
  void disposeListSlots();
  expanded.value = new Set();
  usersByDate.value = new Map();
  selectedDate.value = "";
  selectedUserKey.value = "";
  selectedUserName.value = "";
  records.value = [];
  historyDir.value = "";
}

async function loadDates() {
  const id = ++requestId;
  loading.value = true;
  error.value = "";
  try {
    const data = (await api("GET", `/api/history/dates?from=${encodeURIComponent(from.value)}&to=${encodeURIComponent(to.value)}`)) as { dates?: HistoryDate[] };
    if (id !== requestId) return;
    dates.value = Array.isArray(data?.dates) ? data.dates : [];
    const valid = new Set(dates.value.map(item => item.date));
    for (const value of expanded.value) {
      if (!valid.has(value)) {
        expanded.value.delete(value);
        usersByDate.value.delete(value);
      }
    }
    if (!mobile.value && !expanded.value.size && dates.value.length) expanded.value.add(dates.value[0].date);
    if (!mobile.value && !selectedDate.value && dates.value.length) selectedDate.value = dates.value[0].date;
    if (!selectedDate.value || mobile.value) {
      await disposeListSlots();
      records.value = [];
      historyDir.value = "";
    }
    loading.value = false;
    await Promise.all([...expanded.value].map(value => loadUsers(value, id)));
  } catch (reason) {
    if (id !== requestId || isAbortError(reason)) return;
    loading.value = false;
    error.value = reason instanceof Error ? reason.message : String(reason);
    dates.value = [];
    expanded.value = new Set();
    usersByDate.value = new Map();
  }
}

async function loadUsers(date: string, id = requestId) {
  if (!date || !expanded.value.has(date)) return;
  try {
    const data = (await api("GET", `/api/history/users?date=${encodeURIComponent(date)}`)) as { users?: HistoryUser[] };
    if (id !== requestId || !expanded.value.has(date)) return;
    usersByDate.value.set(date, Array.isArray(data?.users) ? data.users : []);
  } catch (reason) {
    if (id !== requestId || isAbortError(reason)) return;
    usersByDate.value.set(date, []);
  }
}

async function loadRecords() {
  if (!selectedDate.value || !selectedUserKey.value) return;
  const id = ++requestId;
  await disposeListSlots();
  records.value = [];
  try {
    const data = (await api("GET", `/api/history?date=${encodeURIComponent(selectedDate.value)}&userKey=${encodeURIComponent(selectedUserKey.value)}`)) as { historyDir?: string; records?: HistoryRecord[] };
    if (id !== requestId) return;
    historyDir.value = data?.historyDir || "";
    records.value = Array.isArray(data?.records) ? data.records : [];
    await paintListSlots();
  } catch (reason) {
    if (id !== requestId || isAbortError(reason)) return;
    error.value = reason instanceof Error ? reason.message : String(reason);
  }
}

async function toggleDate(date: string) {
  if (expanded.value.has(date)) {
    expanded.value.delete(date);
    usersByDate.value.delete(date);
    if (!selectedUserKey.value && selectedDate.value === date) selectedDate.value = [...expanded.value][0] || "";
    expanded.value = new Set(expanded.value);
    return;
  }
  expanded.value = new Set(expanded.value).add(date);
  if (!selectedUserKey.value) selectedDate.value = date;
  await loadUsers(date);
}

async function chooseUser(date: string, user: HistoryUser) {
  const key = String(user.userKey || user.userId || "");
  if (!key) return;
  selectedDate.value = date;
  selectedUserKey.value = key;
  selectedUserName.value = user.userName || t("history.no_user_specified");
  detail.value = null;
  await loadRecords();
}

function goBack() {
  void disposeListSlots();
  selectedUserKey.value = "";
  selectedUserName.value = "";
  records.value = [];
  historyDir.value = "";
  detail.value = null;
}

async function applyRange() {
  if (!rangeDraftFrom.value || !rangeDraftTo.value || rangeDraftFrom.value > rangeDraftTo.value) return;
  from.value = rangeDraftFrom.value;
  to.value = rangeDraftTo.value;
  rangeOpen.value = false;
  requestId += 1;
  expanded.value = new Set();
  usersByDate.value = new Map();
  selectedDate.value = "";
  selectedUserKey.value = "";
  selectedUserName.value = "";
  await disposeListSlots();
  records.value = [];
  await loadDates();
}

async function disposeListSlots() {
  const slots = historyRoot.value?.querySelectorAll<HTMLElement>('[data-plugin-slot="history.list.badges"]') || [];
  for (const slot of slots) await disposePluginSlot(slot);
}
async function paintListSlots() {
  await nextTick();
  const slots = historyRoot.value?.querySelectorAll<HTMLElement>('[data-plugin-slot="history.list.badges"]') || [];
  for (const slot of slots) {
    await renderPluginSlot(slot, "history.list.badges", {
      mode: "list",
      primaryId: slot.dataset.pluginPrimaryId || "",
    });
  }
}
async function disposeHistorySlots() {
  const slots = historyRoot.value?.querySelectorAll<HTMLElement>("[data-plugin-slot]") || [];
  for (const slot of slots) await disposePluginSlot(slot);
}
function openDetail(record: HistoryRecord) {
  detail.value = record;
}
function closeDetail() {
  detail.value = null;
}

onMounted(() => {
  setTopbarTitle(t("shell.history"));
  updateMobile();
  resetSelectionForMobile();
  resizeHandler = () => {
    const previous = mobile.value;
    updateMobile();
    if (previous !== mobile.value) {
      resetSelectionForMobile();
      void loadDates();
    }
  };
  window.addEventListener("resize", resizeHandler);
  void loadDates();
});

onBeforeUnmount(() => {
  requestId += 1;
  closeDetail();
  void disposeHistorySlots();
  if (resizeHandler) window.removeEventListener("resize", resizeHandler);
});
</script>

<template>
  <main id="view" ref="historyRoot" class="view-root" data-testid="main-view">
    <header class="page-head">
      <div class="page-head-copy"><div class="eyebrow">{{ t("shell.history") }}</div><h2>{{ t("shell.history") }}</h2><p class="page-kicker">{{ formatDate(from) }} {{ t("common.to") }} {{ formatDate(to) }} · {{ selectedUserKey ? `${formatDate(selectedDate)} · ${selectedUser}` : t("history.choose_run_users") }}</p></div>
    </header>
    <NxpEmptyState v-if="loading && !dates.length" :title="t('common.loading')" />
    <NxpEmptyState v-else-if="error && !dates.length" :title="t('api.error.http', { status: 0 }, '历史记录加载失败')" :description="error" tone="danger" />
    <div v-else class="history-browser" :class="{ 'history-detail-visible': mobile && Boolean(selectedUserKey), 'history-user-selected': Boolean(selectedUserKey), 'history-users-visible': mobile && Boolean(selectedDate) && !selectedUserKey }" data-testid="history-panels">
      <div class="history-list-column">
        <div class="history-range-search" data-history-range data-testid="history-range-search">
          <div class="history-range-picker">
            <button id="history-range-display" class="history-range-display" type="button" aria-haspopup="dialog" :aria-expanded="rangeOpen" aria-controls="history-range-popover" data-history-range-display data-testid="history-range-display" @click.stop="rangeOpen ? rangeOpen = false : openRangePicker()"><span data-history-range-label>{{ rangeDisplay }}</span><span class="history-range-icon" aria-hidden="true"><NxpIcon name="calendar" /></span></button>
            <div v-if="rangeOpen" id="history-range-popover" class="history-range-popover secondary-surface" role="dialog" :aria-label="t('history.choose_time_range')" data-history-range-popover @click.stop>
              <div class="history-calendar-toolbar"><button class="ghost sm" type="button" :aria-label="t('history.previous_month')" @click="moveCalendar(-1)">‹</button><strong>{{ calendarTitle }}</strong><button class="ghost sm" type="button" :aria-label="t('history.next_month')" :disabled="!canNextMonth" @click="moveCalendar(1)">›</button></div>
              <div class="history-calendar-months"><section v-for="month in calendarMonths" :key="month" class="history-calendar-month"><h4>{{ monthLabel(month) }}</h4><div class="history-calendar-grid"><span v-for="day in [t('common.sun'), t('common.mon'), t('common.tue'), t('common.wed'), t('common.thu'), t('common.fri'), t('common.sat')]" :key="`${month}-${day}`" class="history-calendar-weekday">{{ day }}</span><template v-for="(day, index) in calendarDays(month)" :key="`${month}-${index}`"><span v-if="day.empty" class="history-calendar-day is-empty" aria-hidden="true"></span><button v-else class="history-calendar-day" :class="{ 'is-start': day.start, 'is-end': day.end, 'is-in-range': day.inRange }" type="button" :disabled="day.value > dateValue(today)" :aria-label="formatDate(day.value)" @click="chooseRangeDate(day.value)">{{ Number(day.value.slice(-2)) }}</button></template></div></section></div>
              <div class="history-range-selection"><span class="history-range-selection-item"><span class="muted">{{ t("common.start") }}</span><strong>{{ rangeDraftFrom.replaceAll("-", "/") }}</strong></span><span class="history-range-selection-arrow" aria-hidden="true">→</span><span class="history-range-selection-item"><span class="muted">{{ t("common.end") }}</span><strong>{{ rangeDraftTo.replaceAll("-", "/") }}</strong></span></div>
              <div class="history-range-popover-footer"><span class="muted history-range-hint">{{ t("history.filter.date_help") }}</span><NxpButton class="primary sm" type="button" @click="applyRange">{{ t("history.apply_range") }}</NxpButton></div>
            </div>
            <input id="history-from" type="hidden" :value="from" :aria-label="`${t('common.start')} ${t('common.date')}`" data-testid="history-from">
            <input id="history-to" type="hidden" :value="to" :aria-label="`${t('common.end')} ${t('common.date')}`" data-testid="history-to">
          </div>
        </div>
        <aside class="history-dates-panel">
          <div class="history-panel-head"><NxpIcon name="calendar" /><h3>{{ t("history.date_list") }}</h3><span class="muted">{{ dates.length }} {{ t("history.days") }}</span></div>
          <div class="history-dates-list">
            <div v-for="item in dates" :key="item.date" class="history-date-group" :class="{ active: expanded.has(item.date) }" data-history-date-group :data-date="item.date" data-testid="history-date-group">
              <button class="history-date-row" :class="{ active: expanded.has(item.date) }" type="button" data-testid="history-date" :data-date="item.date" :aria-expanded="expanded.has(item.date)" @click="toggleDate(item.date)"><NxpIcon :name="expanded.has(item.date) ? 'chevronDown' : 'chevronRight'" /><span>{{ formatDate(item.date) }}</span><span class="muted">{{ item.count }} {{ t("common.items") }}</span></button>
              <div v-if="expanded.has(item.date)" class="history-date-users" :data-date="item.date" data-testid="history-date-users">
                <div v-if="usersByDate.get(item.date) === undefined" class="history-users-loading muted" role="status">{{ t("history.loading_run_users") }}</div>
                <div v-else-if="!usersByDate.get(item.date)?.length" class="history-empty-message"><strong>{{ t("history.filter.no_users_day") }}</strong><span>{{ t("history.choose_another_date") }}</span></div>
                <button v-for="user in usersByDate.get(item.date) || []" :key="user.userKey || user.userId" class="history-user-row" :class="{ active: item.date === selectedDate && (user.userKey || user.userId) === selectedUserKey }" type="button" data-testid="history-user" :data-history-date="item.date" :data-user-key="user.userKey || user.userId" :data-user-name="user.userName || ''" @click="chooseUser(item.date, user)"><span class="history-user-avatar" aria-hidden="true"><NxpIcon name="user" /></span><span class="history-user-main"><strong>{{ user.userName || t("history.no_user_specified") }}</strong></span><span class="history-user-arrow" aria-hidden="true"><NxpIcon name="chevronRight" /></span></button>
              </div>
            </div>
            <div v-if="!dates.length" class="history-dates-empty-message"><strong>{{ t("history.records.empty_range") }}</strong><span>{{ t("history.filter.date_range_retry") }}</span></div>
          </div>
        </aside>
      </div>
      <div class="history-records-column">
        <NxpButton v-if="selectedUserKey" class="history-detail-back ghost" type="button" data-testid="history-user-filter-back" @click="goBack">{{ t("history.back_to_user_list") }}</NxpButton>
        <section class="history-records-panel history-level-panel">
          <div class="history-panel-head"><NxpIcon :name="selectedUserKey ? 'queues' : selectedDate ? 'scripts' : 'history'" /><h3>{{ panelTitle }}</h3><span class="muted" data-testid="history-records-count">{{ panelCount }}</span><button class="history-refresh" type="button" :aria-label="t('history.refresh_records')" data-testid="history-refresh" @click="selectedUserKey ? loadRecords() : loadDates()"><NxpIcon name="refresh" /></button></div>
          <div class="history-entry-list history-level-list">
            <div v-if="!selectedUserKey" class="history-empty-message"><strong>{{ t("history.choose_run_users") }}</strong><span>{{ t("history.filter.user_day_help") }}</span></div>
            <div v-else-if="!records.length" class="history-empty-message">{{ t("history.records.empty_day") }}</div>
            <button v-for="record in records" v-else :key="record.id || `${record.startTime}-${record.scriptName}`" class="history-entry" :class="`history-status-${record.status || 'failed'}`" type="button" data-testid="history-entry" @click="openDetail(record)"><span class="history-entry-bar" aria-hidden="true"></span><span class="history-entry-main"><span class="history-entry-title"><strong>{{ formatDateTime(record.startTime) }} · {{ record.scriptName || "-" }}<template v-if="record.queueName"> · {{ record.queueName }}</template></strong><NxpBadge :tone="statusTone(record.status)">{{ statusLabel(record.status) }}</NxpBadge><NxpBadge v-for="badge in historyBadges(record)" :key="badge.key" :tone="badge.tone" :title="badge.title">{{ badge.label }}</NxpBadge><span class="plugin-slot history-plugin-slot" data-plugin-slot="history.list.badges" data-plugin-anchor="history.list.badges" data-plugin-mode="list" :data-plugin-primary-id="record.id || ''" hidden></span></span><span class="history-entry-path">{{ [historyDir, selectedDate, record.historyDirectory, record.logFile].filter(Boolean).join("\\") }}</span></span><span class="history-entry-arrow" aria-hidden="true"><NxpIcon name="chevronRight" /></span></button>
          </div>
        </section>
        <HistoryDetailModal :record="detail" :fallback-user="selectedUser" @close="closeDetail" />
      </div>
    </div>
  </main>
</template>
