<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { api, isAbortError } from "../../platform/api";
import { formatNumber, t } from "../../platform/i18n";
import { disposePluginSlot } from "@bridge/index";
import { renderPluginSlot } from "@bridge/index";
import { setTopbarTitle } from "../../platform/shell";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpPageHeader from "../../ui/composites/NxpPageHeader.vue";
import NxpSelect from "../../ui/primitives/NxpSelect.vue";
import type { NxpOption } from "../../ui/primitives/NxpSelect.vue";
import RangePicker from "./components/RangePicker.vue";
import HistoryList from "./components/HistoryList.vue";
import HistoryDetail from "./components/HistoryDetail.vue";
import { formatDurationMs, formatHistoryDate, statusLabel } from "./utils/historyFormat";
import type { HistoryDate, HistoryRecord, HistoryStatus, HistorySummary, HistoryUser } from "./utils/historyTypes";

const dateValue = formatHistoryDate;
const today = new Date();
const pad = (value: number) => String(value).padStart(2, "0");
const todayValue = () => `${today.getFullYear()}-${pad(today.getMonth() + 1)}-${pad(today.getDate())}`;
const start = new Date(today);
start.setDate(start.getDate() - 29);

const from = ref(`${start.getFullYear()}-${pad(start.getMonth() + 1)}-${pad(start.getDate())}`);
const to = ref(todayValue());
const dates = ref<HistoryDate[]>([]);
const expanded = ref(new Set<string>());
const usersByDate = ref(new Map<string, HistoryUser[]>());
const selectedDate = ref("");
const selectedUserKey = ref("");
const selectedUserName = ref("");
const records = ref<HistoryRecord[]>([]);
const historyDir = ref("");
const statusFilter = ref<HistoryStatus | "">("");
const summary = ref<HistorySummary | null>(null);
const summaryLoading = ref(false);
const loading = ref(true);
const error = ref("");
const detail = ref<HistoryRecord | null>(null);
const historyRoot = ref<HTMLElement | null>(null);
const mobile = ref(false);
const rangeOpen = ref(false);
let requestId = 0;
const statusValues: HistoryStatus[] = ["success", "failed", "partial", "cancelled", "skipped"];
const statusOptions = computed<NxpOption[]>(() => [
  { value: "", label: t("history.status.all") },
  ...statusValues.map(value => ({ value, label: statusLabel(value) })),
]);
const summaryDaily = computed(() => (summary.value?.daily || []).filter(item => item.totalCount > 0));
const maxDailyCount = computed(() => Math.max(1, ...summaryDaily.value.map(item => item.totalCount)));
let resizeHandler: (() => void) | null = null;

const formatDate = (value: string) => formatHistoryDate(value);

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

function historyStatusQuery() {
  return statusFilter.value ? `&status=${encodeURIComponent(statusFilter.value)}` : "";
}

async function loadSummary(id = requestId) {
  summaryLoading.value = true;
  try {
    const data = (await api("GET", `/api/history/summary?from=${encodeURIComponent(from.value)}&to=${encodeURIComponent(to.value)}${historyStatusQuery()}`)) as Partial<HistorySummary>;
    if (id !== requestId) return;
    summary.value = {
      totalCount: Number(data?.totalCount || 0),
      statusCounts: data?.statusCounts || {},
      totalDurationMs: Number(data?.totalDurationMs || 0),
      averageDurationMs: data?.averageDurationMs ?? null,
      successRate: data?.successRate ?? null,
      daily: Array.isArray(data?.daily) ? data.daily : [],
    };
  } catch (reason) {
    if (id !== requestId || isAbortError(reason)) return;
    summary.value = null;
  } finally {
    if (id === requestId) summaryLoading.value = false;
  }
}

async function loadDates() {
  const id = ++requestId;
  loading.value = true;
  error.value = "";
  void loadSummary(id);
  try {
    const data = (await api("GET", `/api/history/dates?from=${encodeURIComponent(from.value)}&to=${encodeURIComponent(to.value)}${historyStatusQuery()}`)) as { dates?: HistoryDate[] };
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
    const data = (await api("GET", `/api/history/users?date=${encodeURIComponent(date)}${historyStatusQuery()}`)) as { users?: HistoryUser[] };
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
    const data = (await api("GET", `/api/history?date=${encodeURIComponent(selectedDate.value)}&userKey=${encodeURIComponent(selectedUserKey.value)}${historyStatusQuery()}`)) as { historyDir?: string; records?: HistoryRecord[] };
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

async function applyRange(range: { from: string; to: string }) {
  if (!range.from || !range.to || range.from > range.to) return;
  from.value = range.from;
  to.value = range.to;
  rangeOpen.value = false;
  await resetAndReload();
}

async function changeStatus(value: string | string[]) {
  const next = Array.isArray(value) ? String(value[0] || "") : String(value || "");
  if (next === statusFilter.value) return;
  statusFilter.value = statusValues.includes(next as HistoryStatus) ? next as HistoryStatus : "";
  await resetAndReload();
}

async function resetAndReload() {
  requestId += 1;
  expanded.value = new Set();
  usersByDate.value = new Map();
  selectedDate.value = "";
  selectedUserKey.value = "";
  selectedUserName.value = "";
  await disposeListSlots();
  records.value = [];
  summary.value = null;
  await loadDates();
}

function summaryStatusCount(status: HistoryStatus) {
  return Number(summary.value?.statusCounts?.[status] || 0);
}

function summaryRate() {
  return summary.value?.successRate == null ? "-" : `${formatNumber(summary.value.successRate, { maximumFractionDigits: 1 })}%`;
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
    <NxpPageHeader
      :eyebrow="t('shell.history')"
      :title="t('shell.history')"
      :description="`${formatDate(from)} ${t('common.to')} ${formatDate(to)} · ${selectedUserKey ? `${formatDate(selectedDate)} · ${selectedUserName || t('history.no_user_specified')}` : t('history.choose_run_users')}`"
    />
    <NxpEmptyState v-if="loading && !dates.length" :title="t('common.loading')" />
    <NxpEmptyState v-else-if="error && !dates.length" :title="t('api.error.http', { status: 0 }, '历史记录加载失败')" :description="error" tone="danger" />
    <template v-else>
      <section class="history-insights" data-testid="history-summary">
        <div class="history-insights-toolbar">
          <div>
            <span class="eyebrow">{{ t("history.insights.eyebrow") }}</span>
            <h2>{{ t("history.insights.title") }}</h2>
          </div>
          <div class="history-status-filter">
            <label class="field-label" for="history-status-filter-trigger">{{ t("history.status.label") }}</label>
            <NxpSelect
              id="history-status-filter"
              :model-value="statusFilter"
              :options="statusOptions"
              :aria-label="t('history.status.label')"
              @update:model-value="changeStatus"
            />
          </div>
        </div>
        <div v-if="summary" class="history-summary-cards">
          <article class="history-summary-card" data-testid="history-summary-total">
            <span class="k">{{ t("history.summary.total") }}</span>
            <strong class="v">{{ summary.totalCount }}</strong>
          </article>
          <article class="history-summary-card">
            <span class="k">{{ t("history.summary.success_rate") }}</span>
            <strong class="v">{{ summaryRate() }}</strong>
          </article>
          <article class="history-summary-card">
            <span class="k">{{ t("history.summary.average_duration") }}</span>
            <strong class="v">{{ formatDurationMs(summary.averageDurationMs) }}</strong>
          </article>
          <article class="history-summary-card">
            <span class="k">{{ t("history.summary.total_duration") }}</span>
            <strong class="v">{{ formatDurationMs(summary.totalDurationMs) }}</strong>
          </article>
        </div>
        <div v-if="summary" class="history-summary-statuses" data-testid="history-summary-statuses">
          <span v-for="status in statusValues" :key="status" class="history-summary-status">
            <span>{{ statusLabel(status) }}</span><strong>{{ summaryStatusCount(status) }}</strong>
          </span>
        </div>
        <div v-if="summaryLoading && !summary" class="muted history-summary-loading" role="status">{{ t("common.loading") }}</div>
        <section v-if="summary && summaryDaily.length" class="history-trend" data-testid="history-summary-trend">
          <div class="section-heading"><h3>{{ t("history.summary.daily_trend") }}</h3><span class="muted">{{ t("history.summary.days_with_records", { count: summaryDaily.length }) }}</span></div>
          <div class="history-trend-list">
            <div v-for="item in summaryDaily" :key="item.date" class="history-trend-row">
              <span>{{ formatDate(item.date) }}</span>
              <span class="history-trend-track" aria-hidden="true"><span class="history-trend-bar" :style="{ width: `${Math.round(item.totalCount / maxDailyCount * 100)}%` }"></span></span>
              <strong>{{ item.totalCount }}</strong>
            </div>
          </div>
        </section>
      </section>
      <div class="history-browser" :class="{ 'history-detail-visible': mobile && Boolean(selectedUserKey), 'history-user-selected': Boolean(selectedUserKey), 'history-users-visible': mobile && Boolean(selectedDate) && !selectedUserKey }" data-testid="history-panels">
        <div class="history-list-column">
          <RangePicker
            :open="rangeOpen"
            :from="from"
            :to="to"
            @open="rangeOpen = true"
            @close="rangeOpen = false"
            @apply="applyRange"
          />
          <HistoryList
            :dates="dates"
            :expanded="expanded"
            :users-by-date="usersByDate"
            :selected-date="selectedDate"
            :selected-user-key="selectedUserKey"
            :mobile="mobile"
            @toggle-date="toggleDate"
            @choose-user="chooseUser"
          />
        </div>
        <HistoryDetail
          :records="records"
          :history-dir="historyDir"
          :selected-date="selectedDate"
          :selected-user-key="selectedUserKey"
          :selected-user-name="selectedUserName"
          :detail="detail"
          @refresh="selectedUserKey ? loadRecords() : loadDates()"
          @back="goBack"
          @open-detail="openDetail"
          @close-detail="closeDetail"
        />
      </div>
    </template>
  </main>
</template>
