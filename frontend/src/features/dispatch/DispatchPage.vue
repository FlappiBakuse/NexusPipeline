<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { api, isAbortError } from "@legacy/core/api.js";
import { disposePluginSlot, initPluginRuntime, notifyPluginPageEnter, notifyPluginPageLeave, notifyPluginPageUpdated, notifyPluginDispose } from "@legacy/core/plugin-runtime.js";
import { renderPluginSlot } from "@legacy/core/plugin-slots.js";
import { scriptPluginStatus, scriptPluginUnavailableMessage } from "@legacy/core/format.js";
import { state } from "@legacy/core/state.js";
import { t } from "@legacy/core/i18n.js";
import { setTopbarTitle, toast } from "@legacy/core/ui.js";
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";
import NxpSelect, { type NxpOption } from "../../ui/primitives/NxpSelect.vue";
import SystemActionCard from "../dashboard/SystemActionCard.vue";

interface Plugin {
  name?: string;
  kind?: string;
  runtimeEnabled?: boolean;
  state?: string;
}
interface Script {
  id: string;
  name: string;
  pluginType?: string;
}
interface Queue { id: string; name: string }
interface LogEntry { sequence?: number; level?: string; text?: string }
interface RunningRecord {
  id: string;
  targetName?: string;
  kind?: string;
  mode?: string;
  status?: string;
  currentScriptName?: string;
  currentStatus?: string;
  currentAttempt?: number;
  currentMaxAttempts?: number;
  doneTasks?: number;
  totalTasks?: number;
  persistenceWarning?: string;
  logEntries?: LogEntry[];
  logTail?: string[];
}
interface SystemAction { action?: string; deadline?: string; queueName?: string }
interface DispatchStatus { running?: RunningRecord[]; plugins?: Plugin[]; systemAction?: SystemAction | null }
interface PlanTask { scriptName?: string; taskId?: string; userCount?: number }
interface PlanUser { userName?: string; status?: string; successfulRunsToday?: number; maxSuccessfulRunsPerDay?: number; reasonCode?: string; reasonArgs?: Record<string, unknown> }
interface PlanResult {
  targetName?: string;
  admissible?: boolean;
  admissionFailure?: { code?: string; args?: Record<string, unknown> };
  tasks?: PlanTask[];
  users?: PlanUser[];
  warnings?: Array<{ code?: string; args?: Record<string, unknown> }>;
  totalTasks?: number;
  queueClass?: string;
  completionAction?: string;
}

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
const root = ref<HTMLElement | null>(null);
let disposed = false;
let requestSerial = 0;
let pollTimer: ReturnType<typeof setInterval> | null = null;
let statusController: AbortController | null = null;
let pageToken = 0;

const running = computed(() => Array.isArray(status.value.running) ? status.value.running : []);
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
function recordKind(record: RunningRecord) { return t(record.kind === "queue" ? "common.schedule_queues" : "common.script_instance"); }
function recordMode(record: RunningRecord) { return t(record.mode === "auto" ? "common.automatic" : "common.manual"); }
function recordLogs(record: RunningRecord): LogEntry[] {
  if (Array.isArray(record.logEntries)) return record.logEntries;
  return (record.logTail || []).map((text, index) => ({ sequence: index + 1, level: "info", text }));
}
function logClass(level?: string) {
  const normalized = String(level || "info").toLowerCase();
  return ["debug", "info", "warn", "error", "fatal"].includes(normalized) ? `run-log-${normalized}` : "run-log-info";
}
function progress(record: RunningRecord) {
  if (record.kind === "queue" && record.totalTasks) return Math.round((Number(record.doneTasks) || 0) / record.totalTasks * 100);
  if (record.currentAttempt && record.currentMaxAttempts) return Math.round(record.currentAttempt / record.currentMaxAttempts * 100);
  return 0;
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
    status.value = { ...status.value, ...next, running: Array.isArray(next.running) ? next.running : [] };
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
async function cancelRun(runId: string) {
  if (!window.confirm(t("dispatch.cancel.confirm"))) return;
  try {
    await api("POST", "/api/cancel", { runId });
    toast(t("dispatch.cancellation_requested"));
    await refreshStatus();
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
function explainQueueClass(value?: string) {
  const normalized = String(value || "").toLowerCase().replaceAll("-", "_");
  if (normalized === "emulatoronly" || normalized === "emulator_only") return t("dispatch.queue_class_emulator_only");
  if (normalized === "standard") return t("dispatch.queue_class_standard");
  return value || t("dispatch.not_tracked");
}
function explainCompletionAction(value?: string) {
  const labels: Record<string, string> = { none: "common.no_action", exit: "common.exit_application", sleep: "common.sleep", reboot: "common.restart", shutdown: "common.shut_down" };
  return labels[String(value || "none").toLowerCase()] ? t(labels[String(value || "none").toLowerCase()]) : value || t("common.no_action");
}
function explainUserStatus(user: PlanUser): { tone: "ok" | "blue" | "bad"; label: string; today: string; reason: string } {
  const value = user.status || "ready";
  const tone = value === "ready" ? "ok" : value === "skipped" ? "blue" : "bad";
  const label = value === "ready" ? t("dispatch.ready") : value === "skipped" ? t("dispatch.will_skip") : t("dispatch.blocked");
  const today = Number.isInteger(user.successfulRunsToday)
    ? t("dispatch.plan.successful_today", { successful: user.successfulRunsToday, maximum: (user.maxSuccessfulRunsPerDay || 0) > 0 ? user.maxSuccessfulRunsPerDay : t("common.unlimited") })
    : t("dispatch.not_tracked");
  const reason = t(`dispatch.reason.${user.reasonCode || "ready"}`, user.reasonArgs || {}, user.reasonCode || t("dispatch.ready"));
  return { tone, label, today, reason };
}
function explainReason(code?: string, args?: Record<string, unknown>) {
  return t(`dispatch.warning.${code || ""}`, args || {}, code || t("dispatch.needs_attention"));
}

onMounted(async () => {
  disposed = false;
  pageToken = state.routeToken;
  setTopbarTitle(t("dispatch.scheduler"));
  await initPluginRuntime();
  await loadInitial();
  if (disposed) return;
  await notifyPluginPageEnter({ hash: "dispatch", page: "dispatch", segments: ["dispatch"], token: pageToken, container: root.value });
  pollTimer = setInterval(() => void refreshStatus(), 1000);
});
onBeforeUnmount(() => {
  disposed = true;
  requestSerial += 1;
  if (pollTimer) clearInterval(pollTimer);
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
      <header class="page-head"><div class="page-head-copy"><div class="eyebrow">{{ t("dispatch.scheduler") }}</div><h2>{{ t("dispatch.scheduler") }}</h2><p class="page-kicker">{{ t("dispatch.page.help") }}</p></div></header>
      <div class="plugin-slot" data-plugin-slot="dispatch.cards" data-plugin-anchor="dispatch.cards" hidden></div>
      <div id="system-action-area"><SystemActionCard v-if="status.systemAction && status.systemAction.action !== 'exit'" :action="status.systemAction" @cancelled="refreshStatus" /></div>
      <section id="dispatch-running" class="content-section list-surface" data-testid="dispatch-running"><div class="section-heading"><h3>{{ t("common.running") }} ({{ running.length }})</h3><span class="muted">{{ t("dispatch.updates_every_second") }}</span></div><div id="running-list">
        <NxpEmptyState v-if="!running.length" :title="t('dispatch.no_tasks_are_running')" :description="t('dispatch.running.select_help')" />
        <article v-for="record in running" v-else :key="record.id" class="list-item running-item" :data-run-id="record.id"><div class="list-item-head"><div><div class="list-item-title"><strong>{{ record.targetName }}</strong><NxpBadge :tone="record.kind === 'queue' ? 'blue' : 'muted'">{{ recordKind(record) }}</NxpBadge><NxpBadge tone="muted">{{ recordMode(record) }}</NxpBadge><span v-if="record.kind === 'queue'" class="muted done-count">{{ t("dispatch.summary.items", { done: record.doneTasks || 0, total: record.totalTasks || 0 }) }}</span></div></div><NxpButton class="sm danger" type="button" :disabled="busy" @click="cancelRun(record.id)">{{ t("dispatch.cancel_run") }}</NxpButton></div><div class="qk-row">{{ t("dispatch.running.current_attempt", { script: record.currentScriptName || '-', status: record.currentStatus || '', attempt: record.currentAttempt || 0, max: record.currentMaxAttempts || 0 }) }}</div><div v-if="record.persistenceWarning" class="qk-row"><NxpBadge tone="warn">{{ t("common.history.persistence_warning") }}</NxpBadge> {{ record.persistenceWarning }}</div><div class="progress-line"><div :data-progress="progress(record)" :style="{ width: `${Math.max(0, Math.min(100, progress(record)))}%` }"></div></div><div class="running-item-content"><pre class="logbox run-log run-terminal"><span v-if="!recordLogs(record).length" class="run-log-empty">({{ t("dispatch.no_log_output") }})</span><span v-for="entry in recordLogs(record)" :key="entry.sequence || `${entry.text}-${entry.level}`" class="run-log-line" :class="logClass(entry.level)">{{ entry.text || "" }}</span></pre><div class="plugin-slot running-sidecar" data-plugin-slot="dispatch.running.sidecar" data-plugin-anchor="dispatch.running.sidecar" :data-plugin-mode="record.kind === 'queue' ? 'queue' : 'script'" :data-plugin-primary-id="record.id" hidden></div></div></article>
      </div></section>
      <div class="plugin-slot" data-plugin-slot="dispatch.running.badges" data-plugin-anchor="dispatch.running.badges" hidden></div>
      <section class="content-section" aria-labelledby="dispatch-run-heading"><div class="section-heading"><h3 id="dispatch-run-heading">{{ t("dispatch.start_one_run") }}</h3><span class="muted">{{ t("dispatch.plan.target_help") }}</span></div><div class="dispatch-runbar"><div class="field"><label class="field-label" for="dc-kind-trigger">{{ t("dispatch.target_type") }}</label><NxpSelect id="dc-kind" :model-value="kind" :options="[{ value: 'script', label: t('common.script_instance') }, { value: 'queue', label: t('common.schedule_queues') }]" :aria-label="t('dispatch.target_type')" @update:model-value="onKindChange" /></div><div v-if="kind === 'script'" class="field" id="dc-script-wrap"><label class="field-label" for="dc-script-trigger">{{ t("common.script_instance") }}</label><NxpSelect id="dc-script" v-model="scriptId" :options="scriptOptions" :aria-label="t('common.script_instance')" /></div><div v-else class="field" id="dc-queue-wrap"><label class="field-label" for="dc-queue-trigger">{{ t("common.schedule_queues") }}</label><NxpSelect id="dc-queue" v-model="queueId" :options="queueOptions" :aria-label="t('common.schedule_queues')" /></div><div class="control-action"><NxpButton class="ghost" type="button" data-testid="dispatch-explain" :disabled="busy" @click="explainCurrent">{{ t("dispatch.check_run_plan") }}</NxpButton><NxpButton class="primary" type="button" data-testid="dispatch-run" :disabled="busy" @click="runCurrent">{{ t(kind === 'queue' ? 'dispatch.run_queue' : 'dispatch.run_script') }}</NxpButton></div></div></section>
      <div v-if="plan" class="modal-mask" role="presentation"><section class="modal wide secondary-surface" role="dialog" aria-modal="true" :aria-label="t('dispatch.run_plan_check')"><div class="modal-header"><div><h3 class="modal-title">{{ t("dispatch.run_plan_check") }}</h3></div><button class="icon-button modal-close" type="button" :aria-label="t('common.close')" @click.stop="plan = null"><NxpIcon name="close" /></button></div><div class="modal-body"><section class="execution-plan" data-testid="execution-explain-result" role="region" :aria-label="t('dispatch.run_plan_check')"><section class="execution-plan-summary"><div class="execution-plan-summary-main"><span class="execution-plan-summary-label">{{ t("dispatch.target") }}</span><strong>{{ plan.targetName || "" }}</strong><NxpBadge :tone="plan.admissible ? 'ok' : 'bad'">{{ plan.admissible ? t("dispatch.ready_to_run") : t("dispatch.cannot_start_now") }}</NxpBadge></div><div class="execution-plan-summary-stat"><span class="k">{{ t("common.task") }}</span><strong class="execution-plan-stat-value">{{ t("common.unit.tasks", { count: Number.isFinite(Number(plan.totalTasks)) ? Number(plan.totalTasks) : (plan.tasks || []).length }) }}</strong><span class="muted execution-plan-stat-subvalue">{{ explainQueueClass(plan.queueClass) }}</span></div><div class="execution-plan-summary-stat"><span class="k">{{ t("common.completion_action") }}</span><strong class="execution-plan-stat-value">{{ explainCompletionAction(plan.completionAction) }}</strong></div></section><div v-if="plan.admissionFailure" class="callout callout-warning execution-plan-warning"><strong>{{ t(`api.error.${plan.admissionFailure.code || 'admission_failed'}`, plan.admissionFailure.args || {}, plan.admissionFailure.code || t('dispatch.start.unavailable')) }}</strong></div><section v-if="plan.tasks?.length" class="execution-plan-section" role="table"><div class="execution-plan-section-heading"><div class="execution-plan-section-heading-main"><h4>{{ t("common.task_list") }}</h4><NxpBadge tone="muted">{{ t("common.unit.tasks", { count: plan.tasks.length }) }}</NxpBadge></div></div><div class="execution-plan-table-header execution-plan-task-header" role="row"><span role="columnheader">{{ t("common.task") }}</span><span role="columnheader">{{ t("dispatch.users") }}</span></div><div v-for="(task, index) in plan.tasks" :key="`${task.taskId || task.scriptName}-${index}`" class="execution-plan-row execution-plan-task-row" role="row"><div class="execution-plan-cell execution-plan-name" role="cell"><div class="execution-plan-task-main"><span class="execution-plan-task-index" aria-hidden="true">{{ index + 1 }}</span><span class="execution-plan-task-copy"><strong>{{ task.scriptName || task.taskId || "" }}</strong></span></div></div><div class="execution-plan-cell execution-plan-users" role="cell"><NxpBadge tone="blue">{{ t("dispatch.summary.users", { count: task.userCount || 0 }) }}</NxpBadge></div></div></section><section v-if="plan.users?.length" class="execution-plan-section" role="table"><div class="execution-plan-section-heading"><div class="execution-plan-section-heading-main"><h4>{{ t("dispatch.user_eligibility") }}</h4><NxpBadge tone="muted">{{ t("dispatch.summary.users", { count: plan.users.length }) }}</NxpBadge></div></div><div class="execution-plan-table-header execution-plan-user-header" role="row"><span role="columnheader">{{ t("common.user") }}</span><span role="columnheader">{{ t("common.status") }}</span><span role="columnheader">{{ t("dispatch.successful_today") }}</span><span role="columnheader">{{ t("common.reason") }}</span></div><div v-for="(user, index) in plan.users" :key="`${user.userName}-${index}`" class="execution-plan-row execution-plan-user-row" role="row"><div class="execution-plan-cell execution-plan-name" role="cell">{{ user.userName || t("dispatch.unnamed_user") }}</div><div class="execution-plan-cell execution-plan-status" role="cell"><NxpBadge :tone="explainUserStatus(user).tone">{{ explainUserStatus(user).label }}</NxpBadge></div><div class="execution-plan-cell execution-plan-today muted" role="cell">{{ explainUserStatus(user).today }}</div><div class="execution-plan-cell execution-plan-reason muted" role="cell">{{ explainUserStatus(user).reason }}</div></div></section><div v-if="plan.warnings?.length" class="callout callout-warning execution-plan-warning"><strong>{{ t("dispatch.notice") }}</strong><br><span v-for="(warning, index) in plan.warnings" :key="`${warning.code}-${index}`">{{ explainReason(warning.code, warning.args) }}<br></span></div></section></div></section></div>
      <div class="plugin-slot" data-plugin-slot="dispatch.run.sections" data-plugin-anchor="dispatch.run.sections" hidden></div>
    </template>
  </main>
</template>
