<script setup lang="ts">
import { onBeforeUnmount, onMounted, reactive } from "vue";
import { t } from "../../../platform/i18n";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpEmptyState from "../../../ui/primitives/NxpEmptyState.vue";
import NxpScrollArea from "../../../ui/primitives/NxpScrollArea.vue";
import SystemActionCard from "../../dashboard/SystemActionCard.vue";
import { runningLogClass, runningLogEntries, runningProgress, type DispatchRunningRecord, type DispatchSystemAction } from "../utils/dispatchTypes";

/** 运行中区块：宿主系统操作倒计时、运行记录卡片与实时日志。取消动作由页面执行。 */

defineProps<{
  running: DispatchRunningRecord[];
  systemAction: DispatchSystemAction | null;
  busy: boolean;
  executionPreviewLayoutEnabled: boolean;
}>();

const emit = defineEmits<{ cancel: [runId: string]; cancelled: [] }>();

const MIN_LOG_HEIGHT = 180;
const MAX_LOG_HEIGHT = 720;
const LOG_HEIGHT_STEP = 24;
const logHeights = reactive<Record<string, number>>({});
let logResize: { runId: string; startY: number; startHeight: number } | null = null;

function maxLogHeight() {
  const viewportHeight = typeof window === "undefined" ? MAX_LOG_HEIGHT : window.innerHeight;
  return Math.max(MIN_LOG_HEIGHT, Math.min(MAX_LOG_HEIGHT, Math.floor(viewportHeight * 0.75)));
}

function defaultLogHeight() {
  const viewportHeight = typeof window === "undefined" ? MAX_LOG_HEIGHT : window.innerHeight;
  return Math.max(MIN_LOG_HEIGHT, Math.min(360, Math.round(viewportHeight * 0.3)));
}

function logHeight(runId: string) {
  if (typeof logHeights[runId] !== "number") logHeights[runId] = defaultLogHeight();
  return logHeights[runId];
}

function setLogHeight(runId: string, value: number) {
  logHeights[runId] = Math.max(MIN_LOG_HEIGHT, Math.min(maxLogHeight(), Math.round(value)));
}

function startLogResize(event: PointerEvent, runId: string) {
  if (event.button !== 0) return;
  event.preventDefault();
  logResize = { runId, startY: event.clientY, startHeight: logHeight(runId) };
  (event.currentTarget as HTMLElement | null)?.setPointerCapture?.(event.pointerId);
}

function moveLogResize(event: PointerEvent) {
  if (!logResize) return;
  setLogHeight(logResize.runId, logResize.startHeight + event.clientY - logResize.startY);
}

function stopLogResize() {
  logResize = null;
}

function adjustLogHeight(event: KeyboardEvent, runId: string) {
  let next: number | null = null;
  if (event.key === "ArrowDown" || event.key === "PageDown") next = logHeight(runId) + LOG_HEIGHT_STEP;
  if (event.key === "ArrowUp" || event.key === "PageUp") next = logHeight(runId) - LOG_HEIGHT_STEP;
  if (event.key === "Home") next = MIN_LOG_HEIGHT;
  if (event.key === "End") next = maxLogHeight();
  if (next === null) return;
  event.preventDefault();
  setLogHeight(runId, next);
}

function clampLogHeights() {
  for (const [runId, height] of Object.entries(logHeights)) setLogHeight(runId, height);
}

onMounted(() => {
  window.addEventListener("pointermove", moveLogResize);
  window.addEventListener("pointerup", stopLogResize);
  window.addEventListener("pointercancel", stopLogResize);
  window.addEventListener("resize", clampLogHeights);
});

onBeforeUnmount(() => {
  logResize = null;
  window.removeEventListener("pointermove", moveLogResize);
  window.removeEventListener("pointerup", stopLogResize);
  window.removeEventListener("pointercancel", stopLogResize);
  window.removeEventListener("resize", clampLogHeights);
});

function recordKind(record: DispatchRunningRecord) {
  return t(record.kind === "queue" ? "common.schedule_queues" : "common.script_instance");
}
function recordMode(record: DispatchRunningRecord) {
  return t(record.mode === "auto" ? "common.automatic" : "common.manual");
}
</script>

<template>
  <div id="system-action-area">
    <SystemActionCard v-if="systemAction && systemAction.action !== 'exit'" :action="systemAction" @cancelled="emit('cancelled')" />
  </div>
  <section id="dispatch-running" class="content-section list-surface" data-testid="dispatch-running">
    <div class="section-heading">
      <h3>{{ t("common.running") }} ({{ running.length }})</h3>
      <span class="muted">{{ t("dispatch.updates_every_second") }}</span>
    </div>
    <div id="running-list">
      <NxpEmptyState v-if="!running.length" :title="t('dispatch.no_tasks_are_running')" :description="t('dispatch.running.select_help')" />
      <article v-for="record in running" v-else :key="record.id" class="list-item running-item" :data-run-id="record.id">
        <div class="list-item-head">
          <div>
            <div class="list-item-title">
              <strong>{{ record.targetName }}</strong>
              <NxpBadge :tone="record.kind === 'queue' ? 'blue' : 'muted'">{{ recordKind(record) }}</NxpBadge>
              <NxpBadge tone="muted">{{ recordMode(record) }}</NxpBadge>
              <span v-if="record.kind === 'queue'" class="muted done-count">{{ t("dispatch.summary.items", { done: record.doneTasks || 0, total: record.totalTasks || 0 }) }}</span>
            </div>
          </div>
          <NxpButton class="sm danger" type="button" :disabled="busy" @click="emit('cancel', record.id)">{{ t("dispatch.cancel_run") }}</NxpButton>
        </div>
        <div class="qk-row">{{ t("dispatch.running.current_attempt", { script: record.currentScriptName || '-', status: record.currentStatus || '', attempt: record.currentAttempt || 0, max: record.currentMaxAttempts || 0 }) }}</div>
        <div v-if="record.persistenceWarning" class="qk-row">
          <NxpBadge tone="warn">{{ t("common.history.persistence_warning") }}</NxpBadge> {{ record.persistenceWarning }}
        </div>
        <div class="progress-line">
          <div :data-progress="runningProgress(record)" :style="{ width: `${Math.max(0, Math.min(100, runningProgress(record)))}%` }"></div>
        </div>
        <div class="running-item-content" :class="{ 'has-execution-preview': executionPreviewLayoutEnabled }">
          <div class="run-log-resizable" :style="{ height: `${logHeight(record.id)}px` }" :data-log-height="logHeight(record.id)" :data-testid="`run-log-resizable-${record.id}`">
            <NxpScrollArea class="run-log run-terminal" direction="both" :aria-label="t('dispatch.run_log')"><pre class="logbox"><span v-if="!runningLogEntries(record).length" class="run-log-empty">({{ t("dispatch.no_log_output") }})</span><span v-for="entry in runningLogEntries(record)" :key="entry.sequence || `${entry.text}-${entry.level}`" class="run-log-line" :class="runningLogClass(entry.level)">{{ entry.text || "" }}</span></pre></NxpScrollArea>
            <div
              class="run-log-resize-handle"
              role="separator"
              aria-orientation="horizontal"
              tabindex="0"
              :aria-label="t('dispatch.adjust_log_height')"
              :aria-valuemin="MIN_LOG_HEIGHT"
              :aria-valuemax="maxLogHeight()"
              :aria-valuenow="logHeight(record.id)"
              :data-testid="`run-log-resize-handle-${record.id}`"
              @pointerdown="startLogResize($event, record.id)"
              @keydown="adjustLogHeight($event, record.id)"
            ></div>
          </div>
          <div class="plugin-slot running-sidecar" data-plugin-slot="dispatch.running.sidecar" data-plugin-anchor="dispatch.running.sidecar" :data-plugin-mode="record.kind === 'queue' ? 'queue' : 'script'" :data-plugin-primary-id="record.id" hidden></div>
        </div>
      </article>
    </div>
  </section>
</template>
