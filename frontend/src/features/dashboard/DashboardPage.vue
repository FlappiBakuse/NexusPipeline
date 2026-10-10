<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowReactive, watch } from "vue";
import { onBeforeRouteLeave } from "vue-router";
import { api, apiResponse, isAbortError } from "../../platform/api";
import { openEventStream, type EventStreamHandle, type RealtimeSseEvent } from "../../platform/events";
import { registerInterval, state } from "../../platform/page-state";
import { formatList, getLocale, onLocaleChanged, t } from "../../platform/i18n";
import { registerRecoveryDirtyGuard } from "../../platform/service-recovery";
import { onServiceTrafficChanged } from "../../platform/service-traffic";
import { renderPluginSlot } from "@bridge/index";
import { disposePluginSlot } from "@bridge/index";
import { setTopbarTitle } from "../../platform/shell";
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpCard from "../../ui/primitives/NxpCard.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpScrollArea from "../../ui/primitives/NxpScrollArea.vue";
import NxpPageHeader from "../../ui/composites/NxpPageHeader.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import SystemActionCard from "./SystemActionCard.vue";
import DashboardHistorySummary from "./DashboardHistorySummary.vue";
import DashboardPluginCard from "./DashboardPluginCard.vue";
import DashboardLayoutModal from "./DashboardLayoutModal.vue";
import { DashboardLayoutEditor, type DashboardSnapshot } from "./layout-editor";
import { historyTodayValue } from "../history/utils/historyFormat";
import type { HistorySummary } from "../history/utils/historyTypes";

interface RunningRecord {
  id: string;
  targetId?: string;
  targetName?: string;
  kind?: string;
  mode?: string;
  status?: string;
  currentScriptName?: string;
  currentStatus?: string;
  currentAttempt?: number;
  currentMaxAttempts?: number;
  persistenceWarning?: string;
}

interface DashboardStatus {
  version?: string;
  running?: RunningRecord[];
  plugins?: Array<{ configuredEnabled?: boolean; displayName?: string }>;
  systemAction?: { state?: string; action?: string; deadline?: string; queueName?: string } | null;
}

const editor = shallowReactive(new DashboardLayoutEditor({
  read: signal => apiResponse<DashboardSnapshot>("GET", "/api/dashboard/layout", undefined, signal, { "Accept-Language": getLocale() }),
  write: async (baseline, ids, signal) => (await apiResponse<DashboardSnapshot>("PUT", "/api/dashboard/layout",
    { schemaVersion: 1, catalogRevision: baseline.data.catalogRevision, visibleCardIds: ids }, signal,
    { "If-Match": baseline.etag!, "Accept-Language": getLocale() })).data,
}));
const visibleCards = computed(() => editor.visibleIds);
const historyVisible = computed(() => visibleCards.value.includes("core:history-duration"));
let releaseDirty: (() => void) | undefined;
let releaseTraffic: (() => void) | undefined;
let releaseLocale: (() => void) | undefined;
function wakeLayout() { if (!document.hidden) void editor.refresh(); }
function beforeUnload(event: BeforeUnloadEvent) { if (editor.editing || editor.saving) { event.preventDefault(); event.returnValue = ""; } }
onBeforeRouteLeave(() => {
  if (editor.saving) return false;
  if (editor.editing && !window.confirm(t("dashboard.layout.discard_confirm"))) return false;
  editor.cancel(); return true;
});

const status = ref<DashboardStatus>({ running: [], plugins: [] });
const loading = ref(true);
const error = ref("");
const cardsSlot = ref<HTMLElement | null>(null);
const afterRunningSlot = ref<HTMLElement | null>(null);
let timer: ReturnType<typeof setInterval> | null = null;
let requestController: AbortController | null = null;
const historySummary = ref<HistorySummary | null>(null);
const historySummaryLoading = ref(false);
const historySummaryError = ref("");
const initialHistoryRange = recentHistoryRange();
const historyFrom = ref(initialHistoryRange.from);
const historyTo = ref(initialHistoryRange.to);
let historySummaryTimer: ReturnType<typeof setInterval> | null = null;
let historySummaryController: AbortController | null = null;
let disposed = false;
let eventStream: EventStreamHandle | null = null;

function recentHistoryRange(now = new Date()) {
  const end = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const start = new Date(end);
  start.setDate(start.getDate() - 6);
  return { from: historyTodayValue(start), to: historyTodayValue(end) };
}

function tone(value: string | undefined) {
  if (value === "success") return "ok";
  if (value === "running") return "blue";
  if (value === "partial" || value === "cancelled" || value === "skipped") return "warn";
  return "bad";
}

function statusText(value: string | undefined) {
  if (value === "success") return t("common.success");
  if (value === "partial") return t("common.partially_failed");
  if (value === "running") return t("common.running");
  if (value === "cancelled") return t("common.cancelled");
  if (value === "skipped") return t("common.skipped");
  return t("common.failed");
}

function recordType(record: RunningRecord) {
  return t(record.kind === "queue" ? "common.schedule_queues" : "common.script_instance");
}

function recordMode(record: RunningRecord) {
  return t(record.mode === "auto" ? "common.automatic" : "common.manual");
}

function disabledPlugins() {
  return (status.value.plugins || []).filter(plugin => !plugin.configuredEnabled);
}

let slotsPainted = false;
async function paintSlots() {
  await nextTick();
  if (cardsSlot.value) await renderPluginSlot(cardsSlot.value, "dashboard.cards");
  if (afterRunningSlot.value) await renderPluginSlot(afterRunningSlot.value, "dashboard.after-running");
}

async function load() {
  if (disposed || requestController) return;
  const controller = new AbortController();
  requestController = controller;
  try {
    const next = await api("GET", "/api/status", undefined, controller.signal) as DashboardStatus;
    if (disposed || controller.signal.aborted) return;
    status.value = { running: [], plugins: [], ...next };
    loading.value = false;
    error.value = "";
    if (!slotsPainted) { await paintSlots(); slotsPainted = true; }
  } catch (reason) {
    if (controller.signal.aborted || isAbortError(reason)) return;
    loading.value = false;
    error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (requestController === controller) requestController = null;
  }
}

function realtimeData(event: RealtimeSseEvent): Record<string, unknown> {
  return event.data && typeof event.data === "object" ? event.data : {};
}

function realtimeRunningRecord(data: Record<string, unknown>): RunningRecord | null {
  const id = String(data.runId || "").trim();
  if (!id) return null;
  return {
    id,
    targetId: String(data.targetId || ""),
    targetName: String(data.targetName || ""),
    kind: String(data.kind || ""),
    mode: String(data.mode || ""),
    status: String(data.status || ""),
    currentScriptName: String(data.currentScriptName || ""),
    currentStatus: String(data.currentStatus || ""),
    currentAttempt: Number(data.currentAttempt || 0),
    currentMaxAttempts: Number(data.currentMaxAttempts || 0),
    persistenceWarning: String(data.persistenceWarning || ""),
  };
}

function applyRealtimeRunStatus(data: Record<string, unknown>) {
  const record = realtimeRunningRecord(data);
  if (!record) return;
  const current = status.value.running || [];
  status.value = {
    ...status.value,
    running: data.active === false
      ? current.filter(item => item.id !== record.id)
      : current.some(item => item.id === record.id)
        ? current.map(item => item.id === record.id ? { ...item, ...record } : item)
        : [...current, record],
  };
}

function applyRealtimeSystemAction(data: Record<string, unknown>) {
  const actionState = String(data.state || "pending");
  status.value = {
    ...status.value,
    systemAction: ["cancelled", "cleared"].includes(actionState)
      ? null
      : {
          state: actionState,
          action: String(data.action || ""),
          queueName: String(data.queueName || ""),
          deadline: typeof data.deadline === "string" ? data.deadline : undefined,
        },
  };
}

function applyRealtimeEvent(event: RealtimeSseEvent) {
  const data = realtimeData(event);
  if (event.type === "run.status") applyRealtimeRunStatus(data);
  else if (event.type === "system.action") applyRealtimeSystemAction(data);
  else if (event.type === "dashboard.layout.changed") editor.invalidate("layout", data);
  else if (event.type === "dashboard.catalog.changed") editor.invalidate("catalog", data);
}

function startStatusPolling() {
  if (!timer && !disposed) timer = registerInterval(setInterval(() => void load(), 3000));
}

function stopStatusPolling() {
  if (timer) {
    clearInterval(timer);
    state.timers.delete(timer);
    timer = null;
  }
}

function startEventStream() {
  eventStream = openEventStream({
    page: "dashboard",
    onEvent: applyRealtimeEvent,
    onReady: async () => {
      await load();
      await editor.refresh();
      stopStatusPolling();
    },
    onMissed: async () => {
      await load();
      await editor.refresh();
    },
    onDisconnected: () => { editor.disconnect(); startStatusPolling(); },
    onFatal: () => { editor.disconnect(); stopStatusPolling(); },
  });
}

function normalizeHistorySummary(data: Partial<HistorySummary>): HistorySummary {
  return {
    totalCount: Number(data?.totalCount || 0),
    statusCounts: data?.statusCounts || {},
    totalDurationMs: Number(data?.totalDurationMs || 0),
    averageDurationMs: data?.averageDurationMs ?? null,
    successRate: data?.successRate ?? null,
    daily: Array.isArray(data?.daily) ? data.daily : [],
  };
}

async function loadHistorySummary() {
  if (disposed || historySummaryController || !historyVisible.value) return;
  const range = recentHistoryRange();
  historyFrom.value = range.from;
  historyTo.value = range.to;
  historySummaryLoading.value = true;
  historySummaryError.value = "";
  const controller = new AbortController();
  historySummaryController = controller;
  try {
    const data = await api(
      "GET",
      `/api/history/summary?from=${encodeURIComponent(range.from)}&to=${encodeURIComponent(range.to)}`,
      undefined,
      controller.signal,
    ) as Partial<HistorySummary>;
    if (disposed || controller.signal.aborted) return;
    historySummary.value = normalizeHistorySummary(data || {});
  } catch (reason) {
    if (disposed || controller.signal.aborted || isAbortError(reason)) return;
    historySummary.value = null;
    historySummaryError.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (historySummaryController === controller) {
      historySummaryController = null;
      if (!disposed) historySummaryLoading.value = false;
    }
  }
}

onMounted(() => {
  disposed = false;
  setTopbarTitle(t("dashboard.dashboard"));
  void load();
  void loadHistorySummary();
  void editor.refresh();
  releaseDirty = registerRecoveryDirtyGuard(() => editor.editing || editor.saving || editor.pending);
  releaseTraffic = onServiceTrafficChanged(paused => { if (paused) editor.pause(); else void editor.refresh(); });
  releaseLocale = onLocaleChanged(() => { editor.disconnect(); void editor.refresh(); });
  window.addEventListener("focus", wakeLayout);
  window.addEventListener("beforeunload", beforeUnload);
  document.addEventListener("visibilitychange", wakeLayout);
  startStatusPolling();
  historySummaryTimer = registerInterval(setInterval(() => void loadHistorySummary(), 60000));
  startEventStream();
});

onBeforeUnmount(() => {
  disposed = true;
  editor.dispose(); releaseDirty?.(); releaseTraffic?.(); releaseLocale?.();
  window.removeEventListener("focus", wakeLayout);
  window.removeEventListener("beforeunload", beforeUnload);
  document.removeEventListener("visibilitychange", wakeLayout);
  stopStatusPolling();
  eventStream?.close();
  eventStream = null;
  requestController?.abort();
  requestController = null;
  if (historySummaryTimer) {
    clearInterval(historySummaryTimer);
    state.timers.delete(historySummaryTimer);
  }
  historySummaryController?.abort();
  historySummaryController = null;
  if (cardsSlot.value) void disposePluginSlot(cardsSlot.value);
  if (afterRunningSlot.value) void disposePluginSlot(afterRunningSlot.value);
});
watch(historyVisible, visible => { if (visible) void loadHistorySummary(); else historySummaryController?.abort(); });
</script>

<template>
  <main id="view" class="view-root workspace-page page-dashboard" data-testid="main-view">
    <template v-if="loading && !status.version">
      <NxpEmptyState :title="t('common.loading')" />
    </template>
    <template v-else-if="error && !status.version">
      <NxpEmptyState :title="t('dashboard.connection.unavailable')" :description="error" tone="danger" />
    </template>
    <template v-else>
      <NxpPageHeader
        :eyebrow="t('dashboard.dashboard')"
        :title="t('shell.dashboard')"
        :description="t('dashboard.overview.help')"
      ><template #actions><NxpButton tone="primary" :disabled="!editor.etag || editor.reading || editor.editing" @click="editor.begin()">{{ t('dashboard.layout.edit') }}</NxpButton></template></NxpPageHeader>
      <div class="dashboard-content">
        <p v-if="error" class="dashboard-system-note" role="alert">{{ error }}</p>
        <div id="system-action-area"><SystemActionCard v-if="status.systemAction && status.systemAction.action !== 'exit'" :action="status.systemAction" @cancelled="load" /></div>
        <div v-if="editor.error && !editor.editing" class="dashboard-system-note" role="alert"><p>{{ t(`api.error.${editor.error}`) }}</p><NxpButton :busy="editor.reading" @click="editor.refresh()">{{ t('dashboard.layout.read_again') }}</NxpButton></div>
        <NxpEmptyState v-if="editor.committed && !visibleCards.length" :title="t('dashboard.layout.none')" :description="t('dashboard.layout.empty_help')" />
        <div v-if="visibleCards.length" class="dashboard-card-grid">
        <template v-for="cardId in visibleCards" :key="cardId">
        <section v-if="cardId === 'core:status'" id="dashboard-state" class="dashboard-state dashboard-wide-card" :class="(status.running || []).length ? 'running' : 'idle'" data-testid="dashboard-state" aria-live="polite">
          <div class="dashboard-state-copy"><div class="state-label">{{ (status.running || []).length ? t("common.running") : t("dashboard.system_idle") }}</div><h3>{{ (status.running || []).length ? t("dashboard.task_in_progress") : t("dashboard.everything_is_ready") }}</h3><p>{{ (status.running || []).length ? t("dashboard.running.summary", { count: (status.running || []).length }) : t("dashboard.running.empty_help") }}</p></div>
        </section>
        <NxpCard v-else-if="cardId === 'core:running'" class="content-section list-surface dashboard-wide-card" data-testid="running-panel">
          <div class="section-heading"><h3>{{ t("common.running") }}</h3><span class="muted">{{ t("dashboard.active_tasks.count", { count: (status.running || []).length }) }}</span></div>
          <NxpEmptyState v-if="!(status.running || []).length" :title="t('dashboard.idle')" :description="t('dashboard.running.empty')" link-href="#/dispatch" :link-label="t('dashboard.go_to_dispatch')" />
          <template v-else>
            <NxpScrollArea class="table-scroll running-table" direction="horizontal" :aria-label="t('dashboard.running.table')"><table class="data-table"><thead><tr><th scope="col">{{ t("common.task") }}</th><th scope="col">{{ t("dashboard.type") }}</th><th scope="col">{{ t("dashboard.mode") }}</th><th scope="col">{{ t("dashboard.progress") }}</th><th scope="col">{{ t("common.status") }}</th></tr></thead><tbody><tr v-for="record in status.running" :key="record.targetName"><td><strong>{{ record.targetName }}</strong></td><td>{{ recordType(record) }}</td><td>{{ recordMode(record) }}</td><td>{{ record.currentScriptName || "-" }} {{ record.currentStatus || "" }}<br><span class="muted">{{ t("dashboard.running.attempt", { attempt: record.currentAttempt, max: record.currentMaxAttempts }) }}</span></td><td><NxpBadge :tone="tone(record.status)">{{ statusText(record.status) }}</NxpBadge></td></tr></tbody></table></NxpScrollArea>
            <div class="running-records"><article v-for="record in status.running" :key="`mobile-${record.targetName}`" class="running-record"><div class="running-record-head"><strong>{{ record.targetName }}</strong><NxpBadge :tone="tone(record.status)">{{ statusText(record.status) }}</NxpBadge></div><div class="running-record-meta"><span>{{ recordType(record) }}</span><span>{{ recordMode(record) }}</span></div><div class="running-record-progress">{{ record.currentScriptName || "-" }} {{ record.currentStatus || "" }}<br><span class="muted">{{ t("dashboard.running.attempt", { attempt: record.currentAttempt, max: record.currentMaxAttempts }) }}</span><br v-if="record.persistenceWarning"><NxpBadge v-if="record.persistenceWarning" tone="warn">{{ t("dashboard.persistence_warning", { label: t("common.history.persistence_warning"), warning: record.persistenceWarning }) }}</NxpBadge></div></article></div>
          </template>
        </NxpCard>
          <DashboardHistorySummary v-else-if="cardId === 'core:history-duration'" class="dashboard-wide-card"
            :summary="historySummary" :loading="historySummaryLoading" :error="historySummaryError" :from="historyFrom" :to="historyTo" />
          <DashboardPluginCard v-else class="dashboard-wide-card" :card-id="cardId" />
        </template>
        </div>
        <div ref="afterRunningSlot" class="plugin-slot" data-plugin-slot="dashboard.after-running" hidden></div>
        <div ref="cardsSlot" class="plugin-slot" data-plugin-slot="dashboard.cards" hidden></div>
        <section v-if="disabledPlugins().length" class="callout callout-warning callout-actions" data-testid="plugin-health"><p>{{ t("dashboard.plugins.disabled_summary", { count: disabledPlugins().length, plugins: formatList(disabledPlugins().map(plugin => plugin.displayName || "")) }) }}</p><a class="back-link" href="#/plugins">{{ t("dashboard.view_plugins") }}</a></section>
      </div>
    </template>
  </main>
  <DashboardLayoutModal :editor="editor" />
</template>
