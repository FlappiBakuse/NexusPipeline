<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { api, isAbortError } from "../../platform/api";
import { openEventStream, type RealtimeSseEvent, type EventStreamHandle } from "../../platform/events";
import { disposePluginSlot, initPluginRuntime, notifyPluginPageEnter, notifyPluginPageLeave, notifyPluginPageUpdated, notifyPluginDispose } from "@bridge/index";
import { renderPluginSlot } from "@bridge/index";
import { scriptPluginStatus, scriptPluginUnavailableMessage } from "../scripts/utils/pluginStatus";
import { registerInterval, state } from "../../platform/page-state";
import { t } from "../../platform/i18n";
import { setTopbarTitle } from "../../platform/shell";
import { toast } from "../../platform/toast";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpConfirmDialog from "../../ui/composites/NxpConfirmDialog.vue";
import NxpPageHeader from "../../ui/composites/NxpPageHeader.vue";
import NxpSelect, { type NxpOption } from "../../ui/primitives/NxpSelect.vue";
import RunPlanModal from "./components/RunPlanModal.vue";
import RunningExecution from "./components/RunningExecution.vue";
import type { DispatchPlanResult, DispatchRunningRecord, DispatchStatus } from "./utils/dispatchTypes";

interface Script {
  id: string;
  name: string;
  pluginType?: string;
}
interface Queue { id: string; name: string }
type RunningRecord = DispatchRunningRecord;
type PlanResult = DispatchPlanResult;

const status = ref<DispatchStatus>({ running: [], plugins: [] });
const scripts = ref<Script[]>([]);
const queues = ref<Queue[]>([]);
const kind = ref<"script" | "queue">("script");
const scriptId = ref("");
const queueId = ref("");
const loading = ref(true);
const error = ref("");
const busy = ref(false);
const plan = ref<PlanResult | null>(null);
const cancelConfirmRunId = ref<string | null>(null);
const cancelConfirmBusy = ref(false);
const root = ref<HTMLElement | null>(null);
let disposed = false;
let requestSerial = 0;
let pollTimer: ReturnType<typeof setInterval> | null = null;
let statusController: AbortController | null = null;
let pageToken = 0;
let eventStream: EventStreamHandle | null = null;

const running = computed(() => Array.isArray(status.value.running) ? status.value.running : []);
const executionPreviewLayoutEnabled = computed(() =>
  (status.value.plugins || []).some((plugin) =>
    Array.isArray(plugin.capabilities)
      && plugin.capabilities.some((capability) => String(capability || "").toLowerCase() === "execution-preview-client")
      && plugin.configuredEnabled === true
      && plugin.runtimeEnabled === true
      && plugin.hasFrontend === true,
  ),
);
const selectedScript = computed(() => scripts.value.find(item => item.id === scriptId.value));
const scriptOptions = computed<NxpOption[]>(() => [
  { value: "", label: t("common.select.script_instance_option") },
  ...scripts.value.map(script => {
    const pluginState = scriptPluginStatus(script, status.value.plugins || []);
    return {
      value: script.id,
      label: `${script.name}${pluginState.specialized && !pluginState.available ? ` · ${pluginState.missing ? t("common.plugin.unknown_label") : t("common.plugin.unavailable_label")}` : ""}`,
      disabled: pluginState.specialized && !pluginState.available,
      title: pluginState.specialized && !pluginState.available ? scriptPluginUnavailableMessage(script, status.value.plugins || []) : undefined,
    };
  }),
]);
const queueOptions = computed<NxpOption[]>(() => [
  { value: "", label: t("dispatch.plan.queue_hint") },
  ...queues.value.map(queue => ({ value: queue.id, label: queue.name })),
]);

function statusTone(value?: string): "ok" | "warn" | "bad" | "blue" | "muted" {
  if (value === "success") return "ok";
  if (value === "running") return "blue";
  if (["partial", "cancelled", "skipped"].includes(value || "")) return "warn";
  if (value === "failed" || value === "error") return "bad";
  return "muted";
}
function statusLabel(value?: string) {
  if (value === "success") return t("common.success");
  if (value === "partial") return t("common.partially_failed");
  if (value === "running") return t("common.running");
  if (value === "cancelled") return t("common.cancelled");
  if (value === "skipped") return t("common.skipped");
  return t("common.failed");
}
function pluginSlotContext(element: HTMLElement) {
  return {
    mode: element.dataset.pluginMode || "",
    primaryId: element.dataset.pluginPrimaryId || "",
    secondaryId: element.dataset.pluginSecondaryId || "",
  };
}
async function paintPluginSlots() {
  await nextTick();
  const slots = root.value?.querySelectorAll<HTMLElement>("[data-plugin-slot]") || [];
  for (const slot of slots) {
    const name = slot.dataset.pluginSlot;
    if (name) await renderPluginSlot(slot, name, pluginSlotContext(slot));
  }
}
async function disposePluginSlots() {
  const slots = root.value?.querySelectorAll<HTMLElement>("[data-plugin-slot]") || [];
  for (const slot of slots) await disposePluginSlot(slot);
}
async function loadInitial() {
  const id = ++requestSerial;
  loading.value = true;
  error.value = "";
  try {
    const [nextStatus, nextScripts, nextQueues] = await Promise.all([
      api("GET", "/api/status"),
      api("GET", "/api/scripts"),
      api("GET", "/api/queues"),
    ]) as [DispatchStatus, Script[], Queue[]];
    if (disposed || id !== requestSerial) return;
    status.value = { running: [], plugins: [], ...nextStatus };
    scripts.value = Array.isArray(nextScripts) ? nextScripts : [];
    queues.value = Array.isArray(nextQueues) ? nextQueues : [];
    loading.value = false;
    await paintPluginSlots();
  } catch (reason) {
    if (disposed || id !== requestSerial || isAbortError(reason)) return;
    loading.value = false;
    error.value = reason instanceof Error ? reason.message : String(reason);
  }
}
async function refreshStatus() {
  if (disposed || statusController) return;
  const controller = new AbortController();
  statusController = controller;
  try {
    const next = await api("GET", "/api/status", undefined, controller.signal) as DispatchStatus;
    if (disposed || controller.signal.aborted) return;
    status.value = mergeStatusSnapshot(next);
    await paintPluginSlots();
    await notifyPluginPageUpdated({ hash: "dispatch", page: "dispatch", segments: ["dispatch"], token: pageToken, container: root.value });
  } catch (reason) {
    if (disposed || controller.signal.aborted || isAbortError(reason)) return;
    toast(t("dispatch.status_update_failed", { reason: reason instanceof Error ? reason.message : String(reason) }), "error");
  } finally {
    if (statusController === controller) statusController = null;
  }
}
function onKindChange(value: string | string[]) {
  kind.value = value === "queue" ? "queue" : "script";
  plan.value = null;
}
async function runCurrent() {
  if (busy.value) return;
  const selectedId = kind.value === "queue" ? queueId.value : scriptId.value;
  if (!selectedId) {
    toast(t(kind.value === "queue" ? "dispatch.plan.queue_required" : "common.select.script_instance_label"), "error");
    return;
  }
  if (kind.value === "script" && selectedScript.value) {
    const unavailable = scriptPluginUnavailableMessage(selectedScript.value, status.value.plugins || []);
    if (unavailable) { toast(unavailable, "error"); return; }
  }
  busy.value = true;
  try {
    await api("POST", kind.value === "queue" ? "/api/dispatch/queue" : "/api/dispatch/script", kind.value === "queue" ? { queueId: selectedId, mode: "manual" } : { scriptId: selectedId, mode: "manual" });
    toast(t("dispatch.run_started"));
    await refreshStatus();
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  } finally {
    busy.value = false;
  }
}
async function explainCurrent() {
  const selectedId = kind.value === "queue" ? queueId.value : scriptId.value;
  if (!selectedId) {
    toast(t(kind.value === "queue" ? "dispatch.plan.queue_required" : "common.select.script_instance_label"), "error");
    return;
  }
  try {
    const data = await api("POST", `/api/dispatch/explain/${kind.value}`, kind.value === "queue" ? { queueId: selectedId } : { scriptId: selectedId }) as unknown;
    if (data && typeof data === "object" && "result" in data) plan.value = ((data as { result?: PlanResult }).result || null);
    else plan.value = data as PlanResult;
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
function requestCancelRun(runId: string) {
  if (cancelConfirmBusy.value) return;
  cancelConfirmRunId.value = runId;
}

function mergeStatusSnapshot(next: DispatchStatus): DispatchStatus {
  const previous = new Map((status.value.running || []).map(record => [record.id, record]));
  const nextRunning = Array.isArray(next.running) ? next.running : [];
  return {
    ...status.value,
    ...next,
    running: nextRunning.map(record => mergeRunningRecord(previous.get(record.id), record)),
  };
}

function mergeRunningRecord(previous: RunningRecord | undefined, next: RunningRecord): RunningRecord {
  if (!previous) return next;
  const previousEntries = Array.isArray(previous.logEntries) ? previous.logEntries : [];
  const nextEntries = Array.isArray(next.logEntries) ? next.logEntries : [];
  const entries = new Map<number, typeof nextEntries[number]>();
  for (const entry of [...previousEntries, ...nextEntries]) {
    if (typeof entry.sequence === "number" && Number.isFinite(entry.sequence)) entries.set(entry.sequence, entry);
  }
  const mergedEntries = [...entries.values()].sort((left, right) => (left.sequence || 0) - (right.sequence || 0)).slice(-500);
  return {
    ...previous,
    ...next,
    logEntries: mergedEntries.length ? mergedEntries : next.logEntries,
    logTruncated: Boolean(previous.logTruncated || next.logTruncated || entries.size > 500),
  };
}

function realtimeData(event: RealtimeSseEvent): Record<string, unknown> {
  return event.data && typeof event.data === "object" ? event.data : {};
}

function realtimeRunningRecord(data: Record<string, unknown>): RunningRecord | null {
  const id = String(data.runId || "").trim();
  if (!id) return null;
  return {
    id,
    kind: String(data.kind || ""),
    targetName: String(data.targetName || ""),
    targetId: String(data.targetId || ""),
    mode: String(data.mode || ""),
    status: String(data.status || ""),
    currentScriptName: String(data.currentScriptName || ""),
    currentScriptId: String(data.currentScriptId || ""),
    currentStatus: String(data.currentStatus || ""),
    currentAttempt: Number(data.currentAttempt || 0),
    currentMaxAttempts: Number(data.currentMaxAttempts || 0),
    doneTasks: Number(data.doneTasks || 0),
    totalTasks: Number(data.totalTasks || 0),
    persistenceWarning: String(data.persistenceWarning || ""),
    logTruncated: Boolean(data.logTruncated),
  };
}

function applyRealtimeRunStatus(data: Record<string, unknown>) {
  const record = realtimeRunningRecord(data);
  if (!record) return;
  const active = data.active !== false;
  const current = status.value.running || [];
  if (!active) {
    status.value = { ...status.value, running: current.filter(item => item.id !== record.id) };
    return;
  }
  const existing = current.find(item => item.id === record.id);
  const nextRecord = mergeRunningRecord(existing, record);
  status.value = {
    ...status.value,
    running: existing ? current.map(item => item.id === record.id ? nextRecord : item) : [...current, nextRecord],
  };
}

function applyRealtimeRunLog(data: Record<string, unknown>) {
  const runId = String(data.runId || "").trim();
  const incoming = Array.isArray(data.entries) ? data.entries : [];
  if (!runId || !incoming.length) return;
  const current = status.value.running || [];
  const existing = current.find(item => item.id === runId);
  if (!existing) return;
  const entries = [...(existing.logEntries || [])];
  const seen = new Set(entries.map(entry => entry.sequence).filter(sequence => typeof sequence === "number"));
  for (const value of incoming) {
    if (!value || typeof value !== "object") continue;
    const item = value as Record<string, unknown>;
    const sequence = Number(item.sequence);
    if (!Number.isFinite(sequence) || seen.has(sequence)) continue;
    seen.add(sequence);
    entries.push({
      sequence,
      timestamp: typeof item.timestamp === "string" ? item.timestamp : undefined,
      level: String(item.level || "info"),
      text: String(item.formattedText || item.message || ""),
      message: String(item.message || ""),
      formattedText: String(item.formattedText || ""),
    });
  }
  entries.sort((left, right) => (left.sequence || 0) - (right.sequence || 0));
  const truncated = entries.length > 500;
  const nextRecord = { ...existing, logEntries: entries.slice(-500), logTruncated: Boolean(existing.logTruncated || truncated) };
  status.value = { ...status.value, running: current.map(item => item.id === runId ? nextRecord : item) };
}

function applyRealtimeSystemAction(data: Record<string, unknown>) {
  const actionState = String(data.state || "pending");
  if (["cancelled", "cleared"].includes(actionState)) {
    status.value = { ...status.value, systemAction: null };
    return;
  }
  status.value = {
    ...status.value,
    systemAction: {
      action: String(data.action || ""),
      queueName: String(data.queueName || ""),
      deadline: typeof data.deadline === "string" ? data.deadline : undefined,
      state: actionState,
    },
  };
}

async function applyRealtimeEvent(event: RealtimeSseEvent) {
  const data = realtimeData(event);
  if (event.type === "run.status") applyRealtimeRunStatus(data);
  else if (event.type === "run.log") applyRealtimeRunLog(data);
  else if (event.type === "system.action") applyRealtimeSystemAction(data);
  else if (event.type === "host.status" && Array.isArray(data.running)) {
    status.value = { ...status.value, running: data.running.map(item => realtimeRunningRecord(item as Record<string, unknown>)).filter((item): item is RunningRecord => item !== null) };
  }
  await notifyPluginPageUpdated({ hash: "dispatch", page: "dispatch", segments: ["dispatch"], token: pageToken, container: root.value });
}

function startStatusPolling() {
  if (!pollTimer && !disposed) pollTimer = registerInterval(setInterval(() => void refreshStatus(), 1000));
}

function stopStatusPolling() {
  if (pollTimer) {
    clearInterval(pollTimer);
    state.timers.delete(pollTimer);
    pollTimer = null;
  }
}

function startEventStream() {
  eventStream = openEventStream({
    page: "dispatch",
    token: pageToken,
    onEvent: event => applyRealtimeEvent(event),
    onReady: async () => {
      await refreshStatus();
      stopStatusPolling();
    },
    onMissed: async () => {
      await refreshStatus();
    },
    onDisconnected: startStatusPolling,
    onFatal: startStatusPolling,
  });
}
function closeCancelConfirm() {
  if (!cancelConfirmBusy.value) cancelConfirmRunId.value = null;
}
async function confirmCancelRun() {
  const runId = cancelConfirmRunId.value;
  if (!runId || cancelConfirmBusy.value) return;
  cancelConfirmBusy.value = true;
  try {
    await api("POST", "/api/cancel", { runId });
    toast(t("dispatch.cancellation_requested"));
    await refreshStatus();
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  } finally {
    cancelConfirmBusy.value = false;
    cancelConfirmRunId.value = null;
  }
}
function explainQueueClass(value?: string) {
  const normalized = String(value || "").toLowerCase().replaceAll("-", "_");
  if (normalized === "emulatoronly" || normalized === "emulator_only") return t("dispatch.queue_class_emulator_only");
  if (normalized === "standard") return t("dispatch.queue_class_standard");
  return value || t("dispatch.not_tracked");
}

onMounted(async () => {
  disposed = false;
  pageToken = state.routeToken;
  setTopbarTitle(t("dispatch.scheduler"));
  await initPluginRuntime();
  await loadInitial();
  if (disposed) return;
  await notifyPluginPageEnter({ hash: "dispatch", page: "dispatch", segments: ["dispatch"], token: pageToken, container: root.value });
  startStatusPolling();
  startEventStream();
});
onBeforeUnmount(() => {
  disposed = true;
  requestSerial += 1;
  stopStatusPolling();
  eventStream?.close();
  eventStream = null;
  statusController?.abort();
  statusController = null;
  void disposePluginSlots();
  void notifyPluginPageLeave({ hash: "dispatch", page: "dispatch", segments: ["dispatch"] });
  void notifyPluginDispose({ hash: "dispatch", page: "dispatch", segments: ["dispatch"] });
});
</script>

<template>
  <main id="view" ref="root" class="view-root" data-testid="main-view">
    <template v-if="loading && !scripts.length && !queues.length">
      <NxpEmptyState :title="t('common.loading')" />
    </template>
    <NxpEmptyState v-else-if="error && !scripts.length && !queues.length" :title="t('dispatch.load.failed')" :description="error" tone="danger" />
    <template v-else>
      <NxpPageHeader
        :eyebrow="t('dispatch.scheduler')"
        :title="t('dispatch.scheduler')"
        :description="t('dispatch.page.help')"
      />
      <div class="plugin-slot" data-plugin-slot="dispatch.cards" data-plugin-anchor="dispatch.cards" hidden></div>
      <RunningExecution
        :running="running"
        :system-action="status.systemAction || null"
        :busy="busy"
        :execution-preview-layout-enabled="executionPreviewLayoutEnabled"
        @cancel="requestCancelRun"
        @cancelled="refreshStatus"
      />
      <div class="plugin-slot" data-plugin-slot="dispatch.running.badges" data-plugin-anchor="dispatch.running.badges" hidden></div>
      <section class="content-section" aria-labelledby="dispatch-run-heading">
        <div class="section-heading">
          <h3 id="dispatch-run-heading">{{ t("dispatch.start_one_run") }}</h3>
          <span class="muted">{{ t("dispatch.plan.target_help") }}</span>
        </div>
        <div class="dispatch-runbar">
          <div class="field">
            <label class="field-label" for="dc-kind-trigger">{{ t("dispatch.target_type") }}</label>
            <NxpSelect
              id="dc-kind"
              :model-value="kind"
              :options="[{ value: 'script', label: t('common.script_instance') }, { value: 'queue', label: t('common.schedule_queues') }]"
              :aria-label="t('dispatch.target_type')"
              @update:model-value="onKindChange"
            />
          </div>
          <div v-if="kind === 'script'" class="field" id="dc-script-wrap">
            <label class="field-label" for="dc-script-trigger">{{ t("common.script_instance") }}</label>
            <NxpSelect
              id="dc-script"
              v-model="scriptId"
              :options="scriptOptions"
              :aria-label="t('common.script_instance')"
              data-testid="dispatch-script-select"
            />
          </div>
          <div v-else class="field" id="dc-queue-wrap">
            <label class="field-label" for="dc-queue-trigger">{{ t("common.schedule_queues") }}</label>
            <NxpSelect
              id="dc-queue"
              v-model="queueId"
              :options="queueOptions"
              :aria-label="t('common.schedule_queues')"
            />
          </div>
          <div class="control-action">
            <NxpButton class="ghost" type="button" data-testid="dispatch-explain" :disabled="busy" @click="explainCurrent">{{ t("dispatch.check_run_plan") }}</NxpButton>
            <NxpButton class="primary" type="button" data-testid="dispatch-run" :disabled="busy" @click="runCurrent">{{ t(kind === 'queue' ? 'dispatch.run_queue' : 'dispatch.run_script') }}</NxpButton>
          </div>
        </div>
      </section>
      <RunPlanModal :plan="plan" @close="plan = null" />
      <div class="plugin-slot" data-plugin-slot="dispatch.run.sections" data-plugin-anchor="dispatch.run.sections" hidden></div>
    </template>
    <NxpConfirmDialog
      :open="cancelConfirmRunId !== null"
      :title="t('dispatch.cancel_run')"
      :message="t('dispatch.cancel.confirm')"
      :confirm-label="t('dispatch.cancel_run')"
      :cancel-label="t('common.cancel')"
      confirm-tone="danger"
      :busy="cancelConfirmBusy"
      @confirm="confirmCancelRun"
      @cancel="closeCancelConfirm"
      @close="closeCancelConfirm"
    />
  </main>
</template>
