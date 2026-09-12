<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { api, isAbortError } from "../../platform/api";
import { t } from "../../platform/i18n";
import { disposePluginSlot } from "@bridge/index";
import { renderPluginSlot } from "@bridge/index";
import { setTopbarTitle } from "../../platform/shell";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpPageHeader from "../../ui/composites/NxpPageHeader.vue";
import RangePicker from "./components/RangePicker.vue";
import HistoryList from "./components/HistoryList.vue";
import HistoryDetail from "./components/HistoryDetail.vue";
import { formatHistoryDate } from "./utils/historyFormat";
import type { HistoryDate, HistoryRecord, HistoryUser } from "./utils/historyTypes";

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
const loading = ref(true);
const error = ref("");
const detail = ref<HistoryRecord | null>(null);
const historyRoot = ref<HTMLElement | null>(null);
const mobile = ref(false);
const rangeOpen = ref(false);
let requestId = 0;
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

async function applyRange(range: { from: string; to: string }) {
  if (!range.from || !range.to || range.from > range.to) return;
  from.value = range.from;
  to.value = range.to;
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
    <NxpPageHeader
      :eyebrow="t('shell.history')"
      :title="t('shell.history')"
      :description="`${formatDate(from)} ${t('common.to')} ${formatDate(to)} · ${selectedUserKey ? `${formatDate(selectedDate)} · ${selectedUserName || t('history.no_user_specified')}` : t('history.choose_run_users')}`"
    />
    <NxpEmptyState v-if="loading && !dates.length" :title="t('common.loading')" />
    <NxpEmptyState v-else-if="error && !dates.length" :title="t('api.error.http', { status: 0 }, '历史记录加载失败')" :description="error" tone="danger" />
    <div v-else class="history-browser" :class="{ 'history-detail-visible': mobile && Boolean(selectedUserKey), 'history-user-selected': Boolean(selectedUserKey), 'history-users-visible': mobile && Boolean(selectedDate) && !selectedUserKey }" data-testid="history-panels">
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
  </main>
</template>
