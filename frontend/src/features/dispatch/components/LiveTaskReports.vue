<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { api, isAbortError } from "../../../platform/api";
import { openEventStream, type EventStreamHandle } from "../../../platform/events";
import { t } from "../../../platform/i18n";
import TaskReportPanel from "../../history/components/TaskReportPanel.vue";
import type { TaskReport } from "../../history/utils/taskTypes";

const props = defineProps<{ executionId: string; currentScriptId?: string }>();
const reports = ref<TaskReport[]>([]);
const currentReport = computed(() => reports.value.find(report =>
  report.scriptInstanceId === props.currentScriptId && report.lifecycleOutcome === "running"));
const error = ref("");
let revision = -1;
let disposed = false;
let pending = false;
let dirty = false;
let controller: AbortController | null = null;
let stream: EventStreamHandle | null = null;
let fallback: ReturnType<typeof setInterval> | null = null;

async function refresh() {
  if (disposed) return;
  if (pending) { dirty = true; return; }
  pending = true;
  do {
    dirty = false;
    controller = new AbortController();
    try {
      const snapshot = await api("GET", `/api/runs/${encodeURIComponent(props.executionId)}/tasks`, undefined, controller.signal) as { runId: string; revision: number; reports: TaskReport[] };
      if (disposed) return;
      if (snapshot.runId === props.executionId && snapshot.revision >= revision) {
        revision = snapshot.revision;
        reports.value = snapshot.reports;
        error.value = "";
      }
    } catch (reason) {
      if (!disposed && !isAbortError(reason)) error.value = reason instanceof Error ? reason.message : String(reason);
    }
  } while (dirty && !disposed);
  pending = false;
}
function stopPolling() { if (fallback) clearInterval(fallback); fallback = null; }
function startPolling() { if (!fallback && !disposed) fallback = setInterval(() => void refresh(), 2000); }
watch(() => props.currentScriptId, () => { void refresh(); });
onMounted(() => {
  void refresh();
  stream = openEventStream({
    onEvent: event => {
      if (event.type === "task-report-changed" && event.data?.runId === props.executionId && Number(event.data.revision) > revision) return refresh();
    },
    onReady: async () => { await refresh(); stopPolling(); },
    onMissed: () => refresh(),
    onDisconnected: startPolling,
    onFatal: stopPolling,
  });
});
onBeforeUnmount(() => { disposed = true; controller?.abort(); stream?.close(); stopPolling(); });
</script>
<template>
  <p v-if="error" role="status">{{ t('tasks.title') }}: {{ error }}</p>
  <TaskReportPanel v-if="currentReport" :key="currentReport.runId" :report="currentReport" layout="steps" />
</template>
