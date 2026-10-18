<script setup lang="ts">
import { takePreparedData } from "../../platform/navigation-preparation";
import { loadHistoryPage, initialHistoryRange } from "./first-screen";
const initialData = takePreparedData<Awaited<ReturnType<typeof loadHistoryPage>>>("history");
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { api, isAbortError } from "../../platform/api";
import { t } from "../../platform/i18n";
import { disposePluginSlot } from "@bridge/index";
import { renderPluginSlot } from "@bridge/index";
import { setTopbarTitle } from "../../platform/shell";
import NxpPageHeader from "../../ui/composites/NxpPageHeader.vue";
import NxpSelect from "../../ui/primitives/NxpSelect.vue";
import type { NxpOption } from "../../ui/primitives/NxpSelect.vue";
import RangePicker from "./components/RangePicker.vue";
import HistoryList from "./components/HistoryList.vue";
import HistoryDetail from "./components/HistoryDetail.vue";
import { formatHistoryDate, historyUserKey, statusLabel } from "./utils/historyFormat";
import type { HistoryDate, HistoryRecord, HistoryStatus, HistoryUser } from "./utils/historyTypes";

const initialRange = initialHistoryRange();
const from = ref(initialRange.from);
const to = ref(initialRange.to);
const dates = ref<HistoryDate[]>([]);
const expanded = ref(new Set<string>());
const usersByDate = ref(new Map<string, HistoryUser[]>());
const selectedDate = ref("");
const selectedUserKey = ref("");
const selectedUserName = ref("");
const records = ref<HistoryRecord[]>([]);
const recordsLoading = ref(false);
const recordsError = ref("");
const userErrors = ref(new Map<string, string>());
let recordRequestId = 0;
const userRequests = new Map<string, number>();
const statusFilter = ref<HistoryStatus | "">("");
const loading = ref(true);
const error = ref("");
const unsupportedCount = ref(0);
type FormatStatus = { unsupportedCount?: number };
const detail = ref<HistoryRecord | null>(null);
const historyRoot = ref<HTMLElement | null>(null);
const mobile = window.innerWidth <= 820;
const rangeOpen = ref(false);
let requestId = 0;
const statusValues: HistoryStatus[] = ["success", "failed", "partial", "cancelled", "skipped"];
const statusOptions = computed<NxpOption[]>(() => [
  { value: "", label: t("history.status.all") },
  ...statusValues.map(value => ({ value, label: statusLabel(value) })),
]);

const formatDate = (value: string) => formatHistoryDate(value);

function historyStatusQuery() {
  return statusFilter.value ? `&status=${encodeURIComponent(statusFilter.value)}` : "";
}

async function loadDates() {
  const id = ++requestId;
  loading.value = true;
  error.value = "";
  try {
    const data = (await api("GET", `/api/history/dates?from=${encodeURIComponent(from.value)}&to=${encodeURIComponent(to.value)}${historyStatusQuery()}`)) as { dates?: HistoryDate[]; formatStatus?: FormatStatus };
    if (id !== requestId) return;
    unsupportedCount.value = data.formatStatus?.unsupportedCount || 0;
    dates.value = Array.isArray(data?.dates) ? data.dates : [];
    const valid = new Set(dates.value.map(item => item.date));
    for (const value of expanded.value) {
      if (!valid.has(value)) {
        expanded.value.delete(value);
        usersByDate.value.delete(value);
      }
    }
    if (!mobile && !expanded.value.size && dates.value.length) expanded.value.add(dates.value[0].date);
    if (!mobile && !selectedDate.value && dates.value.length) selectedDate.value = dates.value[0].date;
    if (!selectedDate.value || (mobile && !selectedUserKey.value)) {
      await disposeListSlots();
      records.value = [];
      recordsLoading.value = false;
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
  const serial = (userRequests.get(date) || 0) + 1;
  userRequests.set(date, serial);
  userErrors.value.delete(date);
  try {
    const data = (await api("GET", `/api/history/users?date=${encodeURIComponent(date)}${historyStatusQuery()}`)) as { users?: HistoryUser[]; formatStatus?: FormatStatus };
    if (id !== requestId || !expanded.value.has(date) || userRequests.get(date) !== serial) return;
    unsupportedCount.value = data.formatStatus?.unsupportedCount || 0;
    usersByDate.value.set(date, Array.isArray(data?.users) ? data.users : []);
  } catch (reason) {
    if (id !== requestId || userRequests.get(date) !== serial || isAbortError(reason)) return;
    userErrors.value.set(date, reason instanceof Error ? reason.message : String(reason));
  }
}

async function loadRecords() {
  if (!selectedDate.value || !selectedUserKey.value) return;
  const id = ++recordRequestId;
  recordsLoading.value = true;
  recordsError.value = "";
  const date = selectedDate.value;
  const userKey = selectedUserKey.value;
  await disposeListSlots();
  if (id !== recordRequestId) return;
  records.value = [];
  try {
    const data = (await api("GET", `/api/history?date=${encodeURIComponent(date)}&userKey=${encodeURIComponent(userKey)}${historyStatusQuery()}`)) as { records?: HistoryRecord[]; formatStatus?: FormatStatus };
    if (id !== recordRequestId) return;
    unsupportedCount.value = data.formatStatus?.unsupportedCount || 0;

    records.value = Array.isArray(data?.records) ? data.records : [];
    recordsLoading.value = false;
    await paintListSlots();
  } catch (reason) {
    if (id !== recordRequestId || isAbortError(reason)) return;
    recordsError.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (id === recordRequestId) recordsLoading.value = false;
  }
}

async function toggleDate(date: string) {
  if (expanded.value.has(date)) {
    userRequests.set(date, (userRequests.get(date) || 0) + 1);
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
  const key = historyUserKey(user);
  if (!key) return;
  selectedDate.value = date;
  selectedUserKey.value = key;
  selectedUserName.value = user.userName || t("history.no_user_specified");
  detail.value = null;
  await loadRecords();
}

function goBack() {
  recordRequestId += 1;
  void disposeListSlots();
  selectedUserKey.value = "";
  selectedUserName.value = "";
  records.value = [];
  recordsLoading.value = false;
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
  const own = ++requestId;
  recordRequestId += 1;
  recordsLoading.value = false;
  recordsError.value = "";
  userErrors.value.clear();
  userRequests.clear();
  detail.value = null;
  expanded.value = new Set();
  usersByDate.value = new Map();
  selectedDate.value = "";
  selectedUserKey.value = "";
  selectedUserName.value = "";
  await disposeListSlots();
  if (own !== requestId) return;
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

async function openLinkedRecord() {
  const query = new URLSearchParams(window.location.hash.split('?')[1] || '');
  const id = query.get('recordId');
  if (!id) return;
  const own = ++requestId;
  loading.value = true;
  try {
    const data = await api('GET', `/api/history/detail?id=${encodeURIComponent(id)}&metadata=true`) as { record?: HistoryRecord };
    if (own !== requestId) return;
    if (!data.record) throw new Error(t('tasks.error.record_deleted'));
    const record = data.record;
    const day = (record.startTime || '').slice(0, 10);
    from.value = day; to.value = day; statusFilter.value = '';
    selectedDate.value = day; selectedUserKey.value = historyUserKey(record);
    selectedUserName.value = record.userName || '';
    dates.value = [{ date: day, count: 1 }]; expanded.value = new Set([day]);
    usersByDate.value.set(day, [{ userKey: selectedUserKey.value, userName: selectedUserName.value, count: 1 }]);
    records.value = [record]; detail.value = record; error.value = '';
  } catch (reason) {
    if (own === requestId) { detail.value = null; records.value = []; dates.value = []; error.value = t('tasks.error.record_deleted'); }
  } finally { if (own === requestId) loading.value = false; }
}

if (initialData) {
  from.value = initialData.from; to.value = initialData.to;
  dates.value = initialData.dates;
  unsupportedCount.value = initialData.unsupportedCount;
  const first = initialData.dates[0]?.date;
  if (initialData.record) {
    const record = initialData.record;
    selectedDate.value = first || ""; selectedUserKey.value = historyUserKey(record);
    selectedUserName.value = record.userName || "";
    expanded.value = new Set(first ? [first] : []);
    if (first) usersByDate.value.set(first, [{ userKey: selectedUserKey.value, userName: selectedUserName.value, count: 1 }]);
    records.value = [record]; detail.value = record;
  } else if (!mobile && first) {
    selectedDate.value = first; expanded.value.add(first);
    usersByDate.value.set(first, initialData.users);
  }
  loading.value = false;
}

onMounted(() => {
  setTopbarTitle(t("shell.history"));
  if (initialData) return;
  if (new URLSearchParams(window.location.hash.split('?')[1] || '').has('recordId')) void openLinkedRecord();
  else void loadDates();
});

onBeforeUnmount(() => {
  requestId += 1;
  recordRequestId += 1;
  closeDetail();
  void disposeHistorySlots();
});
</script>

<template>
  <p v-if="unsupportedCount" role="status">{{ t("history.unsupported_records", { count: unsupportedCount }) }}</p>
  <main id="view" ref="historyRoot" class="view-root workspace-page page-history history-page" data-testid="main-view">
    <NxpPageHeader
      :eyebrow="t('shell.history')"
      :title="t('shell.history')"
      :description="`${formatDate(from)} ${t('common.to')} ${formatDate(to)}`"
    />

    <div class="browser-layout" :class="{ 'detail-visible': Boolean(selectedUserKey) }" data-testid="history-panels">
      <div class="browser-list-column">
        <div class="history-query-toolbar" data-testid="history-query-toolbar">
          <RangePicker
            :open="rangeOpen"
            :from="from"
            :to="to"
            @open="rangeOpen = true"
            @close="rangeOpen = false"
            @apply="applyRange"
          />
          <div class="history-status-filter" data-testid="history-status-filter">
            <NxpSelect
              id="history-status-filter"
              :model-value="statusFilter"
              :options="statusOptions"
              :aria-label="t('history.status.label')"
              @update:model-value="changeStatus"
            />
          </div>
        </div>
        <HistoryList
          :dates="dates"
          :loading="loading"
          :error="error"
          @retry="loadDates"
          :expanded="expanded"
          :users-by-date="usersByDate"
          :selected-date="selectedDate"
          :selected-user-key="selectedUserKey"
          :user-errors="userErrors"
          @retry-users="loadUsers"
          @toggle-date="toggleDate"
          @choose-user="chooseUser"
        />
      </div>
      <HistoryDetail
        :records="records"
        :loading="recordsLoading"
        :error="recordsError"
        :selected-user-key="selectedUserKey"
        :selected-user-name="selectedUserName"
        :detail="detail"
        @back="goBack"
        @open-detail="openDetail"
        @close-detail="closeDetail"
      />
    </div>
  </main>
</template>
