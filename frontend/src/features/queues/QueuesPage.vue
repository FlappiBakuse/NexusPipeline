<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { api, isAbortError } from "@legacy/core/api.js";
import { t } from "@legacy/core/i18n.js";
import { setTopbarTitle, toast } from "@legacy/core/ui.js";
import { disposePluginSlot, renderPluginSlot, queueRuntimeLimits, scriptPluginStatus, scriptPluginUnavailableMessage } from "../../compat/queueRuntime";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpLoadingState from "../../ui/composites/NxpLoadingState.vue";
import type { NxpOption } from "../../ui/primitives/NxpSelect.vue";
import NxpPager from "../../ui/primitives/NxpPager.vue";
import NxpModal from "../../ui/primitives/NxpModal.vue";
import { vSortable } from "../../ui/sortable";
import { queueCountdown } from "./queueUtils";
import QueueCard from "./QueueCard.vue";
import QueueEditorModal from "./QueueEditorModal.vue";
import type {
  Plugin,
  Queue,
  QueueDraft,
  QueueTask,
  Script,
  TimeSet,
} from "./queueTypes";
import type { QueueEditorOptions } from "./queueTypes";

const queues = ref<Queue[]>([]);
const scripts = ref<Script[]>([]);
const plugins = ref<Plugin[]>([]);
const loading = ref(true);
const error = ref("");
const modalOpen = ref(false);
const confirmOpen = ref(false);
const editing = ref<Queue | null>(null);
const draft = reactive<QueueDraft>({
  id: "",
  name: "",
  autoRunMode: "none",
  completionAction: "none",
  notifyEnabled: false,
  timeSets: [],
  tasks: [],
});
const deleteTarget = ref<Queue | null>(null);
const queuePage = ref(1);
const countdownByQueue = ref<Record<string, string>>({});
const root = ref<HTMLElement | null>(null);
const QUEUE_PAGE_SIZE = 20;
const queueLimits = computed(() => queueRuntimeLimits());
const totalPages = computed(() => Math.max(1, Math.ceil(queues.value.length / QUEUE_PAGE_SIZE)));
const visibleQueues = computed(() => queues.value.slice((queuePage.value - 1) * QUEUE_PAGE_SIZE, queuePage.value * QUEUE_PAGE_SIZE));
const queueAtLimit = computed(() => Boolean(queueLimits.value.maxQueues && queues.value.length >= queueLimits.value.maxQueues));
const timeSetAtLimit = computed(() => Boolean(queueLimits.value.maxTimeSetsPerQueue && draft.timeSets.length >= queueLimits.value.maxTimeSetsPerQueue));
let countdownTimer: ReturnType<typeof setInterval> | null = null;
let disposed = false;

function firstScriptId(queue: Queue) {
  return queue.tasks?.slice().sort((a, b) => a.index - b.index)[0]?.scriptInstanceId || "";
}

const scriptOptions = computed<NxpOption[]>(() => [
  { value: "", label: t("common.select.script_instance_option") },
  ...scripts.value.map((script) => {
    const pluginStatus = scriptPluginStatus(script, plugins.value);
    const unavailable = pluginStatus.specialized && !pluginStatus.available;
    const suffix = unavailable
      ? (pluginStatus.missing ? t("common.plugin.unknown_label") : t("common.plugin.unavailable_label"))
      : (script.logStallTimeoutMinutes === -1 ? t("common.long_running") : "");
    return {
      value: script.id,
      label: `${script.name}${suffix}`,
      disabled: unavailable,
      title: unavailable ? scriptPluginUnavailableMessage(script, plugins.value) : undefined,
    };
  }),
]);
const modeOptions = computed<NxpOption[]>(() => [
  { value: "none", label: t("queues.do_not_run") },
  { value: "scheduled", label: t("queues.run_on_schedule") },
  { value: "startup", label: t("queues.run_at_startup") },
]);
const completionOptions = computed<NxpOption[]>(() => [
  { value: "none", label: t("common.no_action") },
  { value: "exit", label: t("common.exit_application") },
  { value: "sleep", label: t("common.sleep") },
  { value: "reboot", label: t("common.restart") },
  { value: "shutdown", label: t("common.shut_down") },
]);

function resetDraft(queue: Queue | null = null) {
  editing.value = queue;
  draft.id = queue?.id || "";
  draft.name = queue?.name || "";
  draft.autoRunMode = queue?.autoRunMode || "none";
  draft.completionAction = queue?.completionAction || "none";
  draft.notifyEnabled = Boolean(queue?.notifyEnabled);
  draft.timeSets = (queue?.timeSets || []).map((item) => ({
    id: item.id,
    enabled: item.enabled !== false,
    days: Array.isArray(item.days) ? [...item.days] : [],
    time: item.time || "05:30",
  }));
  if (!draft.timeSets.length)
    draft.timeSets.push({
      id: "",
      enabled: true,
      days: [1, 2, 3, 4, 5],
      time: "05:30",
    });
  draft.tasks = (queue?.tasks || [])
    .slice()
    .sort((a, b) => a.index - b.index)
    .map((task, index) => ({
      id: task.id,
      index,
      scriptInstanceId: task.scriptInstanceId,
    }));
}
function openEditor(queue: Queue | null = null) {
  if (!queue && queueAtLimit.value) {
    toast(t("queues.action.create_count", { current: queues.value.length, maximum: queueLimits.value.maxQueues }), "error");
    return;
  }
  resetDraft(queue);
  modalOpen.value = true;
  void paintEditorSlot();
}
function closeEditor() {
  const slot = root.value?.querySelector<HTMLElement>('[data-plugin-slot="queues.editor.sections"]');
  if (slot) void disposePluginSlot(slot);
  modalOpen.value = false;
}
function queueTotalUsers(tasks: QueueTask[] = draft.tasks) {
  return tasks.reduce((sum, task) => {
    const script = scripts.value.find(item => item.id === task.scriptInstanceId);
    return sum + Math.max(1, script?.users?.filter(user => user.enabled !== false).length || 0);
  }, 0);
}
function addTask() {
  const maximum = Number(queueLimits.value.maxQueueTotalUsers) || 0;
  const current = queueTotalUsers();
  if (maximum && current + 1 > maximum) {
    toast(t("queues.validation.user_limit", { current, maximum }), "error");
    return;
  }
  draft.tasks.push({ id: "", index: draft.tasks.length, scriptInstanceId: "" });
}
function removeTask(index: number) {
  draft.tasks.splice(index, 1);
  draft.tasks.forEach((task, taskIndex) => {
    task.index = taskIndex;
  });
}
function addTimeSet() {
  const maximum = Number(queueLimits.value.maxTimeSetsPerQueue) || 0;
  if (maximum && draft.timeSets.length >= maximum) {
    toast(t("queues.validation.schedule_limit", { current: draft.timeSets.length, maximum }), "error");
    return;
  }
  draft.timeSets.push({
    id: "",
    enabled: true,
    days: [1, 2, 3, 4, 5],
    time: "05:30",
  });
}
function removeTimeSet(index: number) {
  if (draft.timeSets.length > 1) draft.timeSets.splice(index, 1);
}
function reorderTimeSets(ids: string[]) {
  const current = draft.timeSets.slice();
  const next = ids.map(id => current[Number(id)]).filter((item): item is TimeSet => Boolean(item));
  if (next.length !== current.length || next.every((item, index) => item === current[index])) return;
  draft.timeSets = next;
}
function reorderTasks(ids: string[]) {
  const current = draft.tasks.slice();
  const next = ids.map(id => current[Number(id)]).filter((item): item is QueueTask => Boolean(item));
  if (next.length !== current.length || next.every((item, index) => item === current[index])) return;
  draft.tasks = next.map((task, index) => ({ ...task, index }));
}
async function reorderQueues(ids: string[]) {
  const currentPage = visibleQueues.value;
  const byId = new Map(queues.value.map(queue => [queue.id, queue]));
  const nextPage = ids.map(id => byId.get(id)).filter((queue): queue is Queue => Boolean(queue));
  if (nextPage.length !== currentPage.length || nextPage.every((queue, index) => queue.id === currentPage[index]?.id)) return;
  const next = queues.value.slice();
  next.splice((queuePage.value - 1) * QUEUE_PAGE_SIZE, currentPage.length, ...nextPage);
  queues.value = next;
  try {
    await api("PUT", "/api/queues/order", { ids: next.map(item => item.id) });
    toast(t("queues.queue_order_saved"));
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
    await load();
  }
}

function queuePluginIssue(queue: Queue) {
  const issues = (queue.tasks || []).map(task => {
    const script = scripts.value.find(item => item.id === task.scriptInstanceId);
    if (!script) return { missing: true, message: t("common.plugin.unknown_label") };
    const status = scriptPluginStatus(script, plugins.value);
    return status.specialized && !status.available
      ? { missing: status.missing, message: scriptPluginUnavailableMessage(script, plugins.value) }
      : null;
  }).filter((item): item is { missing: boolean; message: string } => Boolean(item));
  const missing = issues.filter(item => item.missing).length;
  const unavailable = issues.length - missing;
  if (!issues.length) return null;
  return {
    tone: (missing ? "bad" : "warn") as "bad" | "warn",
    title: issues.map(item => item.message).join("；"),
    label: missing
      ? t("queues.plugin.unknown_count", { count: missing })
      : t("queues.plugin.unavailable_count", { count: unavailable }),
  };
}

function queueNextTriggerLabel(queue: Queue) {
  if (queue.autoRunMode === "startup") return t("queues.schedule.startup");
  if (queue.autoRunMode !== "scheduled") return t("queues.manual_only");
  const countdown = queueCountdown(queue.nextTrigger);
  if (countdown.kind === "about") return t("common.about_to_run");
  if (countdown.kind === "countdown") return t("queues.schedule.starts_in", { duration: countdown.duration });
  return t("queues.schedule.waiting");
}
function refreshCountdowns() {
  const next: Record<string, string> = {};
  for (const queue of queues.value) next[queue.id] = queueNextTriggerLabel(queue);
  countdownByQueue.value = next;
}

async function paintListSlots() {
  await nextTick();
  const slots = root.value?.querySelectorAll<HTMLElement>('[data-plugin-slot="queues.list.badges"]') || [];
  for (const slot of slots) {
    await renderPluginSlot(slot, "queues.list.badges", {
      mode: "list",
      primaryId: slot.dataset.pluginPrimaryId || "",
    });
  }
}
async function paintEditorSlot() {
  await nextTick();
  const slot = root.value?.querySelector<HTMLElement>('[data-plugin-slot="queues.editor.sections"]');
  if (slot) {
    await renderPluginSlot(slot, "queues.editor.sections", {
      mode: draft.id ? "edit" : "create",
      primaryId: draft.id,
    });
  }
}

async function load() {
  loading.value = true;
  error.value = "";
  try {
    const [queueData, scriptData, status] = await Promise.all([
      api("GET", "/api/queues"),
      api("GET", "/api/scripts"),
      api("GET", "/api/status"),
    ]);
    if (disposed) return;
    queues.value = Array.isArray(queueData) ? queueData : [];
    scripts.value = Array.isArray(scriptData) ? scriptData : [];
    plugins.value = Array.isArray((status as { plugins?: Plugin[] })?.plugins)
      ? (status as { plugins: Plugin[] }).plugins
      : [];
    if (queuePage.value > totalPages.value) queuePage.value = totalPages.value;
    refreshCountdowns();
  } catch (reason) {
    if (!disposed && !isAbortError(reason)) error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (!disposed) loading.value = false;
  }
  await paintListSlots();
}
async function save() {
  if (!draft.name.trim()) {
    toast(t("queues.validation.name_required"), "error");
    return;
  }
  const tasks = draft.tasks
    .filter((task) => task.scriptInstanceId)
    .map((task, index) => ({ ...task, index }));
  if (!tasks.length) {
    toast(t("queues.validation.task_required"), "error");
    return;
  }
  const maximumTimeSets = Number(queueLimits.value.maxTimeSetsPerQueue) || 0;
  if (maximumTimeSets && draft.timeSets.length > maximumTimeSets) {
    toast(t("queues.validation.schedule_limit", { current: draft.timeSets.length, maximum: maximumTimeSets }), "error");
    return;
  }
  const maximumUsers = Number(queueLimits.value.maxQueueTotalUsers) || 0;
  const totalUsers = queueTotalUsers(tasks);
  if (maximumUsers && totalUsers > maximumUsers) {
    toast(t("queues.validation.user_limit", { current: totalUsers, maximum: maximumUsers }), "error");
    return;
  }
  const payload = {
    ...draft,
    name: draft.name.trim(),
    tasks,
    timeSets: draft.timeSets,
    autoRunMode: draft.autoRunMode,
    completionAction: draft.completionAction,
  };
  try {
    if (draft.id)
      await api("PUT", `/api/queues/${encodeURIComponent(draft.id)}`, payload);
    else await api("POST", "/api/queues", payload);
    closeEditor();
    toast(t("queues.schedule_queue_saved"));
    await load();
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
function askDelete(queue: Queue) {
  deleteTarget.value = queue;
  confirmOpen.value = true;
}
async function removeQueue() {
  const queue = deleteTarget.value;
  if (!queue) return;
  try {
    await api("DELETE", `/api/queues/${encodeURIComponent(queue.id)}`);
    confirmOpen.value = false;
    deleteTarget.value = null;
    toast(t("queues.schedule_queue_deleted"));
    await load();
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}

onMounted(() => {
  disposed = false;
  setTopbarTitle(t("common.schedule_queues"));
  void load();
  refreshCountdowns();
  countdownTimer = setInterval(refreshCountdowns, 1000);
});
watch(() => queues.value.length, () => {
  if (queuePage.value > totalPages.value) queuePage.value = totalPages.value;
  void paintListSlots();
});
watch(queuePage, () => { void paintListSlots(); });
onBeforeUnmount(() => {
  disposed = true;
  if (countdownTimer) clearInterval(countdownTimer);
  const slots = root.value?.querySelectorAll<HTMLElement>("[data-plugin-slot]") || [];
  for (const slot of slots) void disposePluginSlot(slot);
});
</script>

<template>
  <main id="view" ref="root" class="view-root" data-testid="main-view">
    <header class="page-head">
      <div class="page-head-copy">
        <div class="eyebrow">{{ t("queues.queue_management") }}</div>
        <h2>{{ t("common.schedule_queues") }}</h2>
        <p class="page-kicker">{{ t("queues.page.help") }}</p>
      </div>
      <div class="page-head-actions">
        <NxpButton class="primary" type="button" :disabled="queueAtLimit" @click="openEditor()">{{
          queueAtLimit
            ? t("queues.action.create_count", { current: queues.length, maximum: queueLimits.maxQueues })
            : t("queues.new_schedule_queue")
        }}</NxpButton>
      </div>
    </header>
    <NxpLoadingState v-if="loading" :title="t('common.loading')" test-id="queues-loading" />
    <NxpEmptyState
      v-else-if="error"
      :title="t('queues.load.failed')"
      :description="error"
      tone="danger"
    />
    <NxpEmptyState
      v-else-if="!queues.length"
        :title="t('queues.no_queues_yet')"
        :description="t('queues.page.help')"
      />
    <section v-else class="card list-surface">
      <TransitionGroup v-sortable="{ onDrop: reorderQueues }" name="nxp-card" tag="div" class="script-grid">
        <QueueCard
          v-for="queue in visibleQueues"
          :key="queue.id"
          :queue="queue"
          :data-dnd-id="queue.id"
          :first-script-id="firstScriptId(queue)"
          :next-label="countdownByQueue[queue.id] || queueNextTriggerLabel(queue)"
          :plugin-issue="queuePluginIssue(queue)"
          :translate="t"
          @edit="openEditor"
          @remove="askDelete"
        />
      </TransitionGroup>
      <NxpPager v-model:page="queuePage" :total-pages="totalPages" :total="queues.length" :page-size="QUEUE_PAGE_SIZE" :label="t('common.schedule_queues')" />
    </section>

    <QueueEditorModal
      :open="modalOpen"
      :title="editing ? t('queues.edit_queue') : t('queues.new_schedule_queue')"
      :draft="draft"
      :options="{ mode: modeOptions, completion: completionOptions, scripts: scriptOptions }"
      :translate="t"
      :max-time-sets-per-queue="queueLimits.maxTimeSetsPerQueue"
      :time-set-at-limit="timeSetAtLimit"
      @close="closeEditor"
      @save="save"
      @add-task="addTask"
      @remove-task="removeTask"
      @add-time-set="addTimeSet"
      @remove-time-set="removeTimeSet"
      @reorder-time-sets="reorderTimeSets"
      @reorder-tasks="reorderTasks"
    />
    <NxpModal
      :open="confirmOpen && Boolean(deleteTarget)"
      :title="t('common.delete') + t('common.schedule_queues')"
      panel-class="secondary-surface"
      :close-label="t('common.close', {}, 'Close')"
      @close="confirmOpen = false"
    >
      <p v-if="deleteTarget" class="modal-copy">
        {{ t("queues.confirm_queue_deletion") }}「{{ deleteTarget.name }}」？
      </p>
      <template #footer>
        <NxpButton class="ghost" type="button" @click="confirmOpen = false">{{
          t("common.cancel")
        }}</NxpButton
        ><NxpButton
          class="danger solid"
          type="button"
          data-action="confirm-delete-queue"
          @click="removeQueue"
          >{{ t("common.confirm") }}</NxpButton
        >
      </template>
    </NxpModal>
  </main>
</template>
