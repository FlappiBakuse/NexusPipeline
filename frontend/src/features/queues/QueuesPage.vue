<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { api, isAbortError } from "@legacy/core/api.js";
import { t } from "@legacy/core/i18n.js";
import { setTopbarTitle, toast } from "@legacy/core/ui.js";
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpEntityIcon from "../../ui/primitives/NxpEntityIcon.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";
import NxpSelect, { type NxpOption } from "../../ui/primitives/NxpSelect.vue";
import NxpSwitch from "../../ui/primitives/NxpSwitch.vue";
import NxpTimePicker from "../../ui/primitives/NxpTimePicker.vue";

interface Script {
  id: string;
  name: string;
}
interface QueueTask {
  id?: string;
  index: number;
  scriptInstanceId: string;
}
interface TimeSet {
  id?: string;
  enabled: boolean;
  days: number[];
  time: string;
}
interface Queue {
  id: string;
  name: string;
  autoRunMode?: string;
  completionAction?: string;
  notifyEnabled?: boolean;
  tasks?: QueueTask[];
  timeSets?: TimeSet[];
  nextTrigger?: string;
}
interface Draft {
  id: string;
  name: string;
  autoRunMode: string;
  completionAction: string;
  notifyEnabled: boolean;
  timeSets: TimeSet[];
  tasks: QueueTask[];
}

const queues = ref<Queue[]>([]);
const scripts = ref<Script[]>([]);
const loading = ref(true);
const error = ref("");
const modalOpen = ref(false);
const confirmOpen = ref(false);
const editing = ref<Queue | null>(null);
const draft = reactive<Draft>({
  id: "",
  name: "",
  autoRunMode: "none",
  completionAction: "none",
  notifyEnabled: false,
  timeSets: [],
  tasks: [],
});
const deleteTarget = ref<Queue | null>(null);
const draggingQueueId = ref("");
const draggingTimeSetIndex = ref<number | null>(null);
const draggingTaskIndex = ref<number | null>(null);

function firstScriptId(queue: Queue) {
  return queue.tasks?.slice().sort((a, b) => a.index - b.index)[0]?.scriptInstanceId || "";
}

const scriptOptions = computed<NxpOption[]>(() => [
  { value: "", label: t("common.select.script_instance_option") },
  ...scripts.value.map((script) => ({ value: script.id, label: script.name })),
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
  resetDraft(queue);
  modalOpen.value = true;
}
function addTask() {
  draft.tasks.push({ id: "", index: draft.tasks.length, scriptInstanceId: "" });
}
function removeTask(index: number) {
  draft.tasks.splice(index, 1);
  draft.tasks.forEach((task, taskIndex) => {
    task.index = taskIndex;
  });
}
function addTimeSet() {
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
function toggleDay(timeSet: TimeSet, day: number) {
  timeSet.days = timeSet.days.includes(day)
    ? timeSet.days.filter((item) => item !== day)
    : [...timeSet.days, day].sort((a, b) => a - b);
}
function startTimeSetDrag(event: DragEvent, index: number) {
  draggingTimeSetIndex.value = index;
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", String(index));
  }
}
function clearTimeSetDrag() {
  draggingTimeSetIndex.value = null;
}
function dropTimeSet(targetIndex: number) {
  const sourceIndex = draggingTimeSetIndex.value;
  clearTimeSetDrag();
  if (sourceIndex === null || sourceIndex === targetIndex || sourceIndex < 0 || sourceIndex >= draft.timeSets.length) return;
  const [moved] = draft.timeSets.splice(sourceIndex, 1);
  draft.timeSets.splice(targetIndex, 0, moved);
}
function startTaskDrag(event: DragEvent, index: number) {
  draggingTaskIndex.value = index;
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", String(index));
  }
}
function clearTaskDrag() {
  draggingTaskIndex.value = null;
}
function dropTask(targetIndex: number) {
  const sourceIndex = draggingTaskIndex.value;
  clearTaskDrag();
  if (sourceIndex === null || sourceIndex === targetIndex || sourceIndex < 0 || sourceIndex >= draft.tasks.length) return;
  const [moved] = draft.tasks.splice(sourceIndex, 1);
  draft.tasks.splice(targetIndex, 0, moved);
  draft.tasks.forEach((task, index) => { task.index = index; });
}
function startQueueDrag(event: DragEvent, id: string) {
  draggingQueueId.value = id;
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", id);
  }
}
function clearQueueDrag() {
  draggingQueueId.value = "";
}
async function dropQueue(targetId: string) {
  const sourceId = draggingQueueId.value;
  clearQueueDrag();
  if (!sourceId || sourceId === targetId) return;
  const next = queues.value.slice();
  const sourceIndex = next.findIndex(item => item.id === sourceId);
  const targetIndex = next.findIndex(item => item.id === targetId);
  if (sourceIndex < 0 || targetIndex < 0) return;
  const [moved] = next.splice(sourceIndex, 1);
  next.splice(targetIndex, 0, moved);
  queues.value = next;
  try {
    await api("PUT", "/api/queues/order", { ids: next.map(item => item.id) });
    toast(t("queues.queue_order_saved"));
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
    await load();
  }
}

async function load() {
  loading.value = true;
  error.value = "";
  try {
    const [queueData, scriptData] = await Promise.all([
      api("GET", "/api/queues"),
      api("GET", "/api/scripts"),
    ]);
    queues.value = Array.isArray(queueData) ? queueData : [];
    scripts.value = Array.isArray(scriptData) ? scriptData : [];
  } catch (reason) {
    if (!isAbortError(reason)) error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    loading.value = false;
  }
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
    modalOpen.value = false;
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
  setTopbarTitle(t("common.schedule_queues"));
  void load();
});
</script>

<template>
  <main id="view" class="view-root" data-testid="main-view">
    <header class="page-head">
      <div class="page-head-copy">
        <div class="eyebrow">{{ t("queues.queue_management") }}</div>
        <h2>{{ t("common.schedule_queues") }}</h2>
        <p class="page-kicker">{{ t("queues.page.help") }}</p>
      </div>
      <div class="page-head-actions">
        <NxpButton class="primary" type="button" @click="openEditor()">{{
          t("queues.new_schedule_queue")
        }}</NxpButton>
      </div>
    </header>
    <NxpEmptyState v-if="loading" :title="t('common.loading')" />
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
      ><a class="back-link" href="#/queues" @click.prevent="openEditor()">{{
        t("queues.new_schedule_queue")
      }}</a></NxpEmptyState
    >
    <section v-else class="card list-surface">
      <div class="script-grid">
        <article
          v-for="queue in queues"
          :key="queue.id"
          class="script-card queue-card"
          data-testid="queue-card"
          @dragover.prevent
          @drop="dropQueue(queue.id)"
        >
          <span
            class="drag-handle"
            role="button"
            tabindex="0"
            :aria-label="t('common.reorder.keyboard_help')"
            :title="t('common.drag_to_reorder')"
            draggable="true"
            @dragstart="startQueueDrag($event, queue.id)"
            @dragend="clearQueueDrag"
            ><NxpIcon name="grip" /></span
          ><NxpEntityIcon :id="firstScriptId(queue)" />
          <div class="script-main">
            <button
              class="entity-link"
              type="button"
              data-action="edit-queue"
              :data-id="queue.id"
              :aria-label="t('queues.editor.title', { name: queue.name })"
              @click="openEditor(queue)"
            >
              <span class="scroll-text"
                ><span class="scroll-inner">{{ queue.name }}</span></span
              >
            </button>
            <div class="meta-line queue-meta">
              <NxpBadge tone="muted">{{
                t("common.unit.tasks", { count: queue.tasks?.length || 0 })
              }}</NxpBadge
              ><NxpBadge tone="muted">{{
                queue.completionAction && queue.completionAction !== "none"
                  ? t("queues.completion.label", {
                      action: queue.completionAction,
                    })
                  : t("queues.completion.none")
              }}</NxpBadge
              ><NxpBadge tone="blue">{{
                queue.autoRunMode === "scheduled"
                  ? t("queues.schedule.waiting")
                  : queue.autoRunMode === "startup"
                    ? t("queues.schedule.startup")
                    : t("queues.manual_only")
              }}</NxpBadge
              ><NxpBadge :tone="queue.notifyEnabled ? 'ok' : 'muted'">{{
                queue.notifyEnabled
                  ? t("queues.notification.enabled")
                  : t("queues.notification.disabled")
              }}</NxpBadge>
            </div>
          </div>
          <div class="queue-ops row-actions entity-actions">
            <button
              class="tertiary queue-edit"
              type="button"
              data-action="edit-queue-direct"
              :data-id="queue.id"
              @click="openEditor(queue)"
            >
              {{ t("queues.edit_queue_button") }}</button
            ><button
              class="danger"
              type="button"
              data-action="delete-queue"
              :data-id="queue.id"
              :data-name="queue.name"
              @click="askDelete(queue)"
            >
              {{ t("queues.delete_queue") }}
            </button>
          </div>
        </article>
      </div>
    </section>

    <div v-if="modalOpen" class="modal-mask" role="presentation" data-locked>
      <section
        class="modal wide secondary-surface"
        role="dialog"
        aria-modal="true"
        :aria-label="
          editing ? t('queues.edit_queue') : t('queues.new_schedule_queue')
        "
      >
        <div class="modal-header">
          <div>
            <h3 class="modal-title">
              {{
                editing
                  ? t("queues.edit_queue")
                  : t("queues.new_schedule_queue")
              }}
            </h3>
          </div>
          <button
            class="icon-button modal-close"
            type="button"
            :aria-label="t('common.close')"
            @click="modalOpen = false"
          >
            <NxpIcon name="close" />
          </button>
        </div>
        <div class="modal-body">
          <div class="field">
            <label class="field-label" for="qm-name"
              >{{ t("queues.queue_name") }} <span class="req">*</span></label
            ><input id="qm-name" v-model="draft.name" type="text" />
          </div>
          <div class="form-grid">
            <div class="field">
              <label class="field-label" for="qm-mode-trigger">{{
                t("queues.automatic_run_mode")
              }}</label
              ><NxpSelect
                id="qm-mode"
                v-model="draft.autoRunMode"
                :options="modeOptions"
                :aria-label="t('queues.automatic_run_mode')"
              />
            </div>
            <div class="field">
              <label class="field-label" for="qm-action-trigger">{{
                t("common.completion_action")
              }}</label
              ><NxpSelect
                id="qm-action"
                v-model="draft.completionAction"
                :options="completionOptions"
                :aria-label="t('common.completion_action')"
              />
            </div>
          </div>
          <div class="switch-row settings-option switch-card">
            <div class="switch-copy">
              <strong>{{ t("queues.queue_notifications") }}</strong
              ><span class="muted">{{
                t("queues.notification.override_help")
              }}</span>
            </div>
            <NxpSwitch
              id="qm-notify"
              v-model="draft.notifyEnabled"
              :aria-label="t('queues.queue_notifications')"
            />
          </div>
          <div class="subsection">
            <div class="section-heading">
              <h3>{{ t("queues.schedules") }}</h3>
              <span class="muted">{{
                t("queues.schedule.collapsed_help")
              }}</span>
            </div>
            <div id="qm-timesets" class="timeset-list">
              <details
                v-for="(timeSet, index) in draft.timeSets"
                :key="index"
                class="timeset-card compact-card"
                :open="index === 0"
                @dragover.prevent
                @drop="dropTimeSet(index)"
              >
                <summary class="timeset-summary">
                  <span class="timeset-summary-main"
                    ><span
                      class="drag-handle"
                      role="button"
                      tabindex="0"
                      :aria-label="t('common.reorder.keyboard_help')"
                      :title="t('common.drag_to_reorder')"
                      draggable="true"
                      @dragstart="startTimeSetDrag($event, index)"
                      @dragend="clearTimeSetDrag"
                      ><NxpIcon name="grip" /></span
                    ><strong>{{
                      t("queues.schedule.label", { index: index + 1 })
                    }}</strong
                    ><span class="muted"
                      >{{ timeSet.time || t("queues.time_not_set") }} ·
                      {{
                        timeSet.days.length
                          ? t("common.unit.days", {
                              count: timeSet.days.length,
                            })
                          : t("queues.no_days_selected")
                      }}</span
                    ></span
                  ><span class="timeset-summary-chevron" aria-hidden="true"
                    >⌄</span
                  >
                </summary>
                <div class="timeset-details">
                  <div class="timeset-body">
                    <div class="timeset-layout">
                      <div class="timeset-days">
                        <label class="field-label">{{
                          t("queues.schedule.days_multiple")
                        }}</label>
                        <div
                          class="days-btn-grid"
                          role="group"
                          :aria-label="t('queues.execution_days')"
                        >
                          <button
                            v-for="(name, day) in [
                              t('queues.sunday'),
                              t('queues.monday'),
                              t('queues.tuesday'),
                              t('queues.wednesday'),
                              t('queues.thursday'),
                              t('queues.friday'),
                              t('queues.saturday'),
                            ]"
                            :key="day"
                            class="mode-toggle"
                            type="button"
                            :aria-pressed="timeSet.days.includes(day)"
                            :title="name"
                            :aria-label="name"
                            @click="toggleDay(timeSet, day)"
                          >
                            {{
                              [
                                t("common.sun"),
                                t("common.mon"),
                                t("common.tue"),
                                t("common.wed"),
                                t("common.thu"),
                                t("common.fri"),
                                t("common.sat"),
                              ][day]
                            }}
                          </button>
                        </div>
                      </div>
                      <div class="timeset-time">
                        <label class="field-label" :for="`ts-time-${index}`">{{
                          t("queues.run_time")
                        }}</label
                        ><NxpTimePicker
                          :id="`ts-time-${index}`"
                          v-model="timeSet.time"
                          :aria-label="t('queues.run_time')"
                        />
                      </div>
                    </div>
                    <div class="timeset-actions">
                      <NxpSwitch
                        v-model="timeSet.enabled"
                        :aria-label="t('common.enabled')"
                      /><button
                        class="tertiary"
                        type="button"
                        @click="removeTimeSet(index)"
                      >
                        {{ t("queues.delete_schedule") }}
                      </button>
                    </div>
                  </div>
                </div>
              </details>
            </div>
            <NxpButton class="ghost" type="button" @click="addTimeSet">{{
              t("queues.add_schedule")
            }}</NxpButton>
          </div>
          <div class="subsection">
            <div class="section-heading">
              <h3>{{ t("common.task_list") }}</h3>
              <span class="muted">{{ t("queues.task.order_help") }}</span>
            </div>
            <div v-if="draft.tasks.length" class="tasks-body">
              <div id="qm-tasks">
                <div
                  v-for="(task, index) in draft.tasks"
                  :key="index"
                  class="list-item task-row"
                  @dragover.prevent
                  @drop="dropTask(index)"
                >
                  <span
                    class="drag-handle"
                    role="button"
                    tabindex="0"
                    :aria-label="t('common.reorder.keyboard_help')"
                    draggable="true"
                    @dragstart="startTaskDrag($event, index)"
                    @dragend="clearTaskDrag"
                    ><NxpIcon name="grip" /></span
                  ><NxpSelect
                    :id="`qm-task-${index}`"
                    v-model="task.scriptInstanceId"
                    :options="scriptOptions"
                    :aria-label="
                      t('queues.task.script_instance', { index: index + 1 })
                    "
                  /><NxpButton
                    class="sm danger"
                    type="button"
                    @click="removeTask(index)"
                    >{{ t("common.delete") }}</NxpButton
                  >
                </div>
              </div>
            </div>
            <NxpButton class="ghost" type="button" @click="addTask">{{
              t("queues.add_task")
            }}</NxpButton>
          </div>
        </div>
        <div class="modal-footer">
          <NxpButton class="ghost" type="button" @click="modalOpen = false">{{
            t("common.cancel")
          }}</NxpButton
          ><NxpButton class="primary" type="button" @click="save">{{
            t("common.save")
          }}</NxpButton>
        </div>
      </section>
    </div>
    <div
      v-if="confirmOpen && deleteTarget"
      class="modal-mask"
      role="presentation"
    >
      <section
        class="modal secondary-surface"
        role="dialog"
        aria-modal="true"
        :aria-label="t('common.delete')"
      >
        <div class="modal-header">
          <div>
            <h3 class="modal-title">
              {{ t("common.delete") }}{{ t("common.schedule_queues") }}
            </h3>
          </div>
          <button
            class="icon-button modal-close"
            type="button"
            :aria-label="t('common.close')"
            @click="confirmOpen = false"
          >
            <NxpIcon name="close" />
          </button>
        </div>
        <div class="modal-body">
          <p class="modal-copy">
            {{ t("queues.confirm_queue_deletion") }}「{{
              deleteTarget.name
            }}」？
          </p>
        </div>
        <div class="modal-footer">
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
        </div>
      </section>
    </div>
  </main>
</template>
