<script setup lang="ts">
import { fieldHelp as vHelp } from '../components/field-help';
import { createIdentity } from '../control/identity';
import { computed, ref } from 'vue';
import type { AutomationBundle } from '../control/wire.generated';
import type { AutomationStore } from '../app/store-ports';
import { t } from '../app/locale';
import { useEditorNotifications } from './editor-notification-choices';
import QueueTasks from './QueueTasks.vue';

type Queue = AutomationBundle['queues'][number];
type Schedule = AutomationBundle['schedules'][number];
const props = defineProps<{
  modelValue: Queue;
  schedules: readonly Schedule[];
  bundle: AutomationBundle;
  store: AutomationStore;
}>();
const emit = defineEmits<{
  'update:modelValue': [value: Queue];
  'update:schedules': [value: readonly Schedule[]];
  discardNewItem: [id: string];
}>();
const expanded = ref(new Set(props.schedules.filter(item => !props.bundle.schedules.some(saved => saved.id === item.id)).map(item => item.id)));
const mode = computed(() =>
  props.modelValue.runOnStartup
    ? 'startup'
    : (props.modelValue.scheduleEnabled ?? props.schedules.some((item) => item.enabled))
      ? 'schedule'
      : 'none'
);
const notifications = useEditorNotifications(props.store, props.store.report);
function update<K extends keyof Queue>(key: K, value: Queue[K]): void {
  emit('update:modelValue', { ...props.modelValue, [key]: value });
}
function setMode(value: string): void {
  emit('update:modelValue', {
    ...props.modelValue,
    runOnStartup: value === 'startup',
    scheduleEnabled: value === 'schedule'
  });
}
function setSchedule(id: string, changes: Partial<Schedule>): void {
  emit(
    'update:schedules',
    props.schedules.map((item) => (item.id === id ? { ...item, ...changes } : item))
  );
}
function addSchedule(): void {
  const id = createIdentity();
  expanded.value = new Set([...expanded.value, id]);
  emit('update:schedules', [
    ...props.schedules,
    {
      id,
      queueId: props.modelValue.id,
      enabled: true,
      weekdays: [1, 2, 3, 4, 5],
      localMinute: 330,
      timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone,
      missedPolicy: 'skip',
      order: props.schedules.length
    }
  ]);
}
function removeSchedule(id: string): void {
  emit('update:schedules', props.schedules.filter(item => item.id !== id));
  if (!props.bundle.schedules.some(item => item.id === id)) emit('discardNewItem', id);
}
function toggle(id: string): void {
  const next = new Set(expanded.value);
  if (next.has(id)) next.delete(id);
  else next.add(id);
  expanded.value = next;
}
function day(id: string, current: readonly number[], value: number): void {
  setSchedule(id, {
    weekdays: current.includes(value) ? current.filter((item) => item !== value) : [...current, value].sort()
  });
}
function time(value: number): string {
  return String(Math.floor(value / 60)).padStart(2, '0') + ':' + String(value % 60).padStart(2, '0');
}
function setTime(id: string, value: string): void {
  const [hour, minute] = value.split(':').map(Number);
  setSchedule(id, { localMinute: hour! * 60 + minute! });
}
</script>

<template>
  <div class="queue-fields">
    <label class="required">
      <span>
        {{ t('队列名称') }}
        <em>*</em>
      </span>
      <nexus-text-input
        data-draft-field="QueueFields/modelValue.name"
        :aria-label="t('队列名称')"
        aria-required="true"
        :value.prop="modelValue.name"
        :max-length.prop.camel="120"
        @nexus-change="update('name', $event.detail.value)"
      />
    </label>
    <div class="columns">
      <label>
        {{ t('自动运行方式') }}
        <FormSelect
        data-draft-field="QueueFields/mode" :value="mode" @change="setMode(($event.target as HTMLSelectElement).value)">
          <option value="none">{{ t('不运行') }}</option>
          <option value="schedule">{{ t('定时运行') }}</option>
          <option value="startup">{{ t('启动时运行') }}</option>
        </FormSelect>
      </label>
      <label>
        {{ t('运行完成操作') }}
        <FormSelect
        data-draft-field="QueueFields/modelValue.completionAction"
          :value="modelValue.completionAction"
          @change="
            update(
              'completionAction',
              ($event.target as HTMLSelectElement).value as Queue['completionAction']
            )
          "
        >
          <option value="none">{{ t('无操作') }}</option>
          <option value="shutdown">{{ t('关机') }}</option>
          <option value="hibernate">{{ t('休眠') }}</option>
          <option value="restart">{{ t('重启') }}</option>
          <option value="exit">{{ t('退出程序') }}</option>
        </FormSelect>
      </label>
    </div>
    <nexus-switch-setting
        data-draft-field="QueueFields/modelValue.notifyEnabled"
      class="notification-setting"
      data-surface="raised"
      :title="t('队列通知')"
      :description="t('开启后仅发送队列汇总，不再发送队列内用户通知；未选择通知任务时不发送。')"
      :value.prop="modelValue.notifyEnabled"
      @nexus-change="update('notifyEnabled', $event.detail.value)"
    />
    <label>
      {{ t('通知任务') }}
      <nexus-select
        data-draft-field="QueueFields/modelValue.notificationTaskIds"
        :aria-label="t('通知任务')"
        :multiple.prop="true"
        :value.prop="modelValue.notificationTaskIds"
        :options.prop="notifications?.map((item) => ({ id: item.id, label: item.name })) || []"
        :placeholder="t('不发送通知')"
        @nexus-change="update('notificationTaskIds', $event.detail.value)"
      />
    </label>
    <section class="queue-section">
      <header>
        <h3 tabindex="0" v-help="t('默认收起；展开后编辑周期与执行时间，拖拽左侧把手排序')">{{ t('定时列表') }}</h3>
      </header>
      <nexus-sortable-list
        :ids.prop="schedules.map((item) => item.id)"
        @nexus-reorder="
          emit(
            'update:schedules',
            $event.detail.ids.map((id: string, order: number) => ({
              ...schedules.find((item) => item.id === id)!,
              order
            }))
          )
        "
      >
        <article
          v-for="(item, index) in schedules"
          :key="item.id"
          :data-item-id="item.id" :data-draft-item="item.id"
          class="schedule-item"
        >
          <div class="schedule-heading">
            <nexus-drag-handle :item-id="item.id" :label="t('调整定时顺序')" />
            <button type="button" :aria-expanded="expanded.has(item.id)" @click="toggle(item.id)">
              <strong>{{ t('定时') }} {{ index + 1 }}</strong>
              <span>{{ time(item.localMinute) }} · {{ item.weekdays.length }} {{ t('天') }}</span>
              <span class="schedule-toggle-hint" aria-hidden="true">{{ t(expanded.has(item.id) ? '点击收回' : '点击展开') }}</span>
            </button>
          </div>
          <div v-if="expanded.has(item.id)" class="schedule-content">
            <div class="schedule-controls">
              <div class="weekday-field" role="group" :aria-label="t('执行周期（可多选）')">
                <span>{{ t('执行周期（可多选）') }}</span>
                <span class="weekdays">
                  <button
                    v-for="(name, number) in ['日', '一', '二', '三', '四', '五', '六']"
                    :key="number"
                    type="button"
                    :aria-pressed="item.weekdays.includes(number)"
                    @click="day(item.id, item.weekdays, number)"
                  >
                    {{ t(name) }}
                  </button>
                </span>
              </div>
              <label>
                {{ t('执行时间') }}
                <nexus-time-picker
                  :aria-label="t('执行时间')"
                  :value.prop="time(item.localMinute)"
                  @nexus-change="setTime(item.id, $event.detail.value)"
                />
              </label>
            </div>
            <div class="end-actions">
              <nexus-switch
        data-draft-field="QueueFields/item.enabled"
                :aria-label="t('启用定时')"
                :value.prop="item.enabled"
                @nexus-change="setSchedule(item.id, { enabled: $event.detail.value })"
              />
              <button
                type="button"
                @click="removeSchedule(item.id)"
              >
                {{ t('删除定时') }}
              </button>
            </div>
          </div>
        </article>
      </nexus-sortable-list>
      <button type="button" @click="addSchedule">+ {{ t('添加定时') }}</button>
    </section>
    <QueueTasks
      :model-value="modelValue"
      :scripts="bundle.scripts"
      @update:model-value="emit('update:modelValue', $event)"
    />
  </div>
</template>

<style scoped>
.queue-fields {
  display: grid;
  gap: 20px;
}
label,
.weekday-field {
  display: grid;
  gap: 6px;
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
}
.required::first-line {
  color: var(--muted);
}
em {
  color: var(--bad);
  font-style: normal;
}
.columns {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
}
.notification-setting {
  padding: 12px 16px;
  border: 1px solid var(--border);
  border-radius: var(--nx-radius-md);
  box-shadow: var(--nx-surface-shadow);
}
.queue-section {
  border-top: 1px solid var(--border);
  padding-top: 20px;
}
header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 20px;
}
h3 {
  font-size: 15px;
  line-height: 1.35;
  margin: 0;
}
small {
  color: var(--muted);
  font-size: 12px;
}
.schedule-item {
  padding: 8px 0 14px;
  border-radius: var(--nx-radius-md);
  transition: background-color var(--nx-motion-duration-fast, 160ms) ease;
}
@media (hover: hover) {
  .schedule-item:hover {
    background-color: var(--content-control-hover);
  }
}
.schedule-item:has(.schedule-heading > button:focus-visible) {
  background-color: var(--content-control-hover);
}
@media (prefers-reduced-motion: reduce) {
  .schedule-item { transition: none; }
}
.schedule-heading {
  display: flex;
  align-items: center;
  gap: 8px;
}
.schedule-heading nexus-drag-handle {
  --nx-drag-size: 32px;
}
.schedule-heading > button {
  display: flex;
  gap: 6px;
  align-items: center;
  flex: 1;
  text-align: left;
  border: 0;
  background: transparent;
  padding: 8px 8px 8px 0;
  box-shadow: none;
}
.schedule-heading span {
  color: var(--muted);
}
.schedule-heading > button:hover { background: transparent; }
.schedule-toggle-hint {
  flex: none;
  margin-inline-start: auto;
  font-size: 12px;
}
.schedule-content {
  padding: 14px 0 0 34px;
}
.schedule-controls {
  display: grid;
  grid-template-columns: minmax(0, 3fr) minmax(150px, 1fr);
  gap: 16px;
}
.weekdays {
  display: flex;
  gap: 4px;
}
.weekdays button {
  flex: 1;
  padding: 0 6px;
}
.weekdays [aria-pressed='true'] {
  border-color: var(--accent);
  background: var(--accent-soft);
  color: var(--accent);
}
.end-actions {
  display: flex;
  align-items: center;
  justify-content: end;
  gap: 12px;
  margin-top: 24px;
}
.queue-section > button,
.end-actions > button {
  color: var(--muted);
}
@media (max-width: 600px) {
  .columns,
  .schedule-controls {
    grid-template-columns: minmax(0, 1fr);
  }
  header {
    flex-wrap: wrap;
  }
  .schedule-content {
    padding-left: 0;
  }
  .weekdays { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); }
}
</style>
