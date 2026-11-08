<script setup lang="ts">
import { createIdentity } from '../control/identity';
import { computed, ref, onBeforeUnmount, onMounted } from 'vue';
import type { AutomationBundle } from '../control/wire.generated';
import type { AutomationStore } from '../app/store-ports';
import { t } from '../app/locale';
import { notificationCatalog, type NotificationTask } from './notification-types';
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
}>();
const expanded = ref(new Set(props.schedules.filter(item => !props.bundle.schedules.some(saved => saved.id === item.id)).map(item => item.id)));
const mode = computed(() =>
  props.modelValue.runOnStartup
    ? 'startup'
    : (props.modelValue.scheduleEnabled ?? props.schedules.some((item) => item.enabled))
      ? 'schedule'
      : 'none'
);
const notifications = ref<NotificationTask[]>([]);
const owner = new AbortController();
onMounted(async () => {
  if (!props.store.can('notifications.read')) return;
  try {
    notifications.value = (
      await props.store.client.read('/control/v1/notifications', notificationCatalog, owner.signal)
    ).tasks;
  } catch (reason) {
    if (!owner.signal.aborted) props.store.report(reason);
  }
});
onBeforeUnmount(() => owner.abort());
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
        <FormSelect :value="mode" @change="setMode(($event.target as HTMLSelectElement).value)">
          <option value="none">{{ t('不运行') }}</option>
          <option value="schedule">{{ t('定时运行') }}</option>
          <option value="startup">{{ t('启动时运行') }}</option>
        </FormSelect>
      </label>
      <label>
        {{ t('运行完成操作') }}
        <FormSelect
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
      class="notification-setting"
      :title="t('队列通知')"
      :description="t('开启后仅发送队列汇总，不再发送队列内用户通知；未选择通知任务时不发送。')"
      :value.prop="modelValue.notifyEnabled"
      @nexus-change="update('notifyEnabled', $event.detail.value)"
    />
    <label>
      {{ t('通知任务') }}
      <nexus-select
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
        <h3>{{ t('定时列表') }}</h3>
        <small>{{ t('默认收起；展开后编辑周期与执行时间，拖拽左侧把手排序') }}</small>
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
          :data-item-id="item.id"
          class="schedule-item"
        >
          <div class="schedule-heading">
            <nexus-drag-handle :item-id="item.id" :label="t('调整定时顺序')" />
            <button type="button" :aria-expanded="expanded.has(item.id)" @click="toggle(item.id)">
              <strong>{{ t('定时') }} {{ index + 1 }}</strong>
              <span>{{ time(item.localMinute) }} · {{ item.weekdays.length }} {{ t('天') }}</span>
              <span class="chevron">⌄</span>
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
                :aria-label="t('启用定时')"
                :value.prop="item.enabled"
                @nexus-change="setSchedule(item.id, { enabled: $event.detail.value })"
              />
              <button
                type="button"
                @click="
                  emit(
                    'update:schedules',
                    schedules.filter((value) => value.id !== item.id)
                  )
                "
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

<style scoped src="./queue-fields.css"></style>
