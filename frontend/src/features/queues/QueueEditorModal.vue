<script setup lang="ts">
import { computed, ref, watch } from "vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";
import NxpModal from "../../ui/primitives/NxpModal.vue";
import NxpSelect from "../../ui/primitives/NxpSelect.vue";
import NxpSwitch from "../../ui/primitives/NxpSwitch.vue";
import NxpTimePicker from "../../ui/primitives/NxpTimePicker.vue";
import { vSortable } from "../../ui/sortable";
import type { QueueDraft, QueueEditorOptions, QueueTranslator } from "./queueTypes";

const props = defineProps<{
  open: boolean;
  title: string;
  draft: QueueDraft;
  options: QueueEditorOptions;
  translate: QueueTranslator;
  maxTimeSetsPerQueue?: number;
  timeSetAtLimit?: boolean;
}>();

const emit = defineEmits<{
  close: [];
  save: [];
  addTask: [];
  removeTask: [index: number];
  addTimeSet: [];
  removeTimeSet: [index: number];
  reorderTimeSets: [ids: string[]];
  reorderTasks: [ids: string[]];
}>();

const dayNames = computed(() => [
  props.translate("queues.sunday"),
  props.translate("queues.monday"),
  props.translate("queues.tuesday"),
  props.translate("queues.wednesday"),
  props.translate("queues.thursday"),
  props.translate("queues.friday"),
  props.translate("queues.saturday"),
]);
const dayShortNames = computed(() => [
  props.translate("common.sun"),
  props.translate("common.mon"),
  props.translate("common.tue"),
  props.translate("common.wed"),
  props.translate("common.thu"),
  props.translate("common.fri"),
  props.translate("common.sat"),
]);

const expandedTimeSetKeys = ref<string[]>([]);
const timeSetKey = (timeSet: QueueDraft["timeSets"][number], index: number) =>
  timeSet.id || `new-time-set-${index}`;
const timeSetKeys = computed(() =>
  props.draft.timeSets.map((timeSet, index) => timeSetKey(timeSet, index)),
);

function syncExpandedTimeSets() {
  const validKeys = new Set(timeSetKeys.value);
  const next = expandedTimeSetKeys.value.filter(key => validKeys.has(key));
  if (!next.length && timeSetKeys.value.length) next.push(timeSetKeys.value[0]);
  if (next.length !== expandedTimeSetKeys.value.length || next.some((key, index) => key !== expandedTimeSetKeys.value[index])) {
    expandedTimeSetKeys.value = next;
  }
}

function isTimeSetOpen(key: string) {
  return expandedTimeSetKeys.value.includes(key);
}

function toggleTimeSet(key: string) {
  expandedTimeSetKeys.value = isTimeSetOpen(key)
    ? expandedTimeSetKeys.value.filter(item => item !== key)
    : [...expandedTimeSetKeys.value, key];
}

watch(timeSetKeys, syncExpandedTimeSets, { immediate: true });

function toggleDay(timeSet: QueueDraft["timeSets"][number], day: number) {
  timeSet.days = timeSet.days.includes(day)
    ? timeSet.days.filter(item => item !== day)
    : [...timeSet.days, day].sort((left, right) => left - right);
}
</script>

<template>
  <NxpModal
    :open="open"
    :title="title"
    size="wide"
    :locked="true"
    panel-class="secondary-surface"
    :close-label="translate('common.close', {}, 'Close')"
    data-locked
    @close="emit('close')"
  >
    <div class="field">
        <label class="field-label" for="qm-name"
          >{{ translate("queues.queue_name") }} <span class="req">*</span></label
        ><input id="qm-name" v-model="draft.name" type="text" />
    </div>
    <div class="form-grid">
        <div class="field">
          <label class="field-label" for="qm-mode-trigger">{{
            translate("queues.automatic_run_mode")
          }}</label
          ><NxpSelect
            id="qm-mode"
            v-model="draft.autoRunMode"
            :options="options.mode"
            :aria-label="translate('queues.automatic_run_mode')"
          />
        </div>
        <div class="field">
          <label class="field-label" for="qm-action-trigger">{{
            translate("common.completion_action")
          }}</label
          ><NxpSelect
            id="qm-action"
            v-model="draft.completionAction"
            :options="options.completion"
            :aria-label="translate('common.completion_action')"
          />
        </div>
    </div>
    <div class="switch-row settings-option switch-card">
        <div class="switch-copy">
          <strong>{{ translate("queues.queue_notifications") }}</strong
          ><span class="muted">{{
            translate("queues.notification.override_help")
          }}</span>
        </div>
        <NxpSwitch
          id="qm-notify"
          v-model="draft.notifyEnabled"
          :aria-label="translate('queues.queue_notifications')"
        />
    </div>
    <div class="subsection">
        <div class="section-heading">
          <h3>{{ translate("queues.schedules") }}</h3>
          <span class="muted">{{
            translate("queues.schedule.collapsed_help")
          }}</span>
        </div>
        <div
          id="qm-timesets"
          v-sortable="{ onDrop: (ids: string[]) => emit('reorderTimeSets', ids) }"
          class="timeset-list"
        >
          <article
            v-for="(timeSet, index) in draft.timeSets"
            :key="timeSetKey(timeSet, index)"
            class="timeset-card compact-card"
            :class="{ 'is-open': isTimeSetOpen(timeSetKey(timeSet, index)) }"
            :data-dnd-id="String(index)"
          >
            <div class="timeset-head">
              <span
                class="drag-handle"
                role="button"
                tabindex="0"
                :aria-label="translate('common.reorder.keyboard_help')"
                :title="translate('common.drag_to_reorder')"
                ><NxpIcon name="grip" /></span>
              <button
                class="timeset-summary"
                type="button"
                data-testid="queue-timeset-toggle"
                :aria-expanded="isTimeSetOpen(timeSetKey(timeSet, index))"
                @click="toggleTimeSet(timeSetKey(timeSet, index))"
              >
                <span class="timeset-summary-main"
                ><strong>{{
                  translate("queues.schedule.label", { index: index + 1 })
                }}</strong
                ><span class="muted"
                  >{{ timeSet.time || translate("queues.time_not_set") }} ·
                  {{
                    timeSet.days.length
                      ? translate("common.unit.days", {
                          count: timeSet.days.length,
                        })
                      : translate("queues.no_days_selected")
                  }}</span
                ></span
                ><span class="timeset-summary-chevron" aria-hidden="true">⌄</span>
              </button>
            </div>
            <Transition name="nxp-collapse">
              <div v-if="isTimeSetOpen(timeSetKey(timeSet, index))" class="timeset-details" data-testid="queue-timeset-body">
              <div class="timeset-body">
                <div class="timeset-layout">
                  <div class="timeset-days">
                    <label class="field-label">{{
                      translate("queues.schedule.days_multiple")
                    }}</label>
                    <div
                      class="days-btn-grid"
                      role="group"
                      :aria-label="translate('queues.execution_days')"
                    >
                      <button
                        v-for="(name, day) in dayNames"
                        :key="day"
                        class="mode-toggle"
                        type="button"
                        :aria-pressed="timeSet.days.includes(day)"
                        :title="name"
                        :aria-label="name"
                        @click="toggleDay(timeSet, day)"
                      >
                        {{ dayShortNames[day] }}
                      </button>
                    </div>
                  </div>
                  <div class="timeset-time">
                    <label class="field-label" :for="'ts-time-' + index">{{
                      translate("queues.run_time")
                    }}</label
                    ><NxpTimePicker
                      :id="'ts-time-' + index"
                      v-model="timeSet.time"
                      :aria-label="translate('queues.run_time')"
                    />
                  </div>
                </div>
                <div class="timeset-actions">
                  <NxpSwitch
                    v-model="timeSet.enabled"
                    :aria-label="translate('common.enabled')"
                  /><button
                    class="tertiary"
                    type="button"
                    @click="emit('removeTimeSet', index)"
                  >
                    {{ translate("queues.delete_schedule") }}
                  </button>
                </div>
              </div>
              </div>
            </Transition>
          </article>
        </div>
        <NxpButton
          class="ghost"
          type="button"
          :disabled="timeSetAtLimit"
          @click="emit('addTimeSet')"
          >{{
            timeSetAtLimit
              ? translate("queues.action.add_time_set", {
                  current: draft.timeSets.length,
                  maximum: maxTimeSetsPerQueue,
                })
              : translate("queues.add_schedule")
          }}</NxpButton
        >
    </div>
    <div class="subsection">
        <div class="section-heading">
          <h3>{{ translate("common.task_list") }}</h3>
          <span class="muted">{{ translate("queues.task.order_help") }}</span>
        </div>
        <div v-if="draft.tasks.length" class="tasks-body">
          <div
            id="qm-tasks"
            v-sortable="{ onDrop: (ids: string[]) => emit('reorderTasks', ids) }"
          >
            <div
              v-for="(task, index) in draft.tasks"
              :key="task.id || 'new-task-' + index"
              class="list-item task-row"
              :data-dnd-id="String(index)"
            >
              <span
                class="drag-handle"
                role="button"
                tabindex="0"
                :aria-label="translate('common.reorder.keyboard_help')"
                ><NxpIcon name="grip" /></span
              ><NxpSelect
                :id="'qm-task-' + index"
                v-model="task.scriptInstanceId"
                :options="options.scripts"
                :aria-label="
                  translate('queues.task.script_instance', { index: index + 1 })
                "
              /><NxpButton
                class="sm danger"
                type="button"
                @click="emit('removeTask', index)"
                >{{ translate("common.delete") }}</NxpButton
              >
            </div>
          </div>
        </div>
        <NxpButton class="ghost" type="button" @click="emit('addTask')">{{
          translate("queues.add_task")
        }}</NxpButton>
    </div>
    <div
        class="plugin-slot queue-editor-plugin-slot"
        data-plugin-slot="queues.editor.sections"
        data-plugin-anchor="queues.editor.sections"
        :data-plugin-mode="draft.id ? 'edit' : 'create'"
        :data-plugin-primary-id="draft.id"
        hidden
    ></div>
    <template #footer>
      <NxpButton class="ghost" type="button" @click="emit('close')">{{
        translate("common.cancel")
      }}</NxpButton
      ><NxpButton class="primary" type="button" @click="emit('save')">{{
        translate("common.save")
      }}</NxpButton>
    </template>
  </NxpModal>
</template>
