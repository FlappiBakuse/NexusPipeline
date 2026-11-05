<script setup lang="ts">
import { createIdentity } from '../control/identity';
import { ref, watch } from 'vue';
import type { AutomationBundle } from '../control/wire.generated';
import { t } from '../app/locale';

type Queue = AutomationBundle['queues'][number];
const props = defineProps<{ modelValue: Queue; scripts: AutomationBundle['scripts'] }>();
const emit = defineEmits<{ 'update:modelValue': [value: Queue] }>();
const rows = ref(props.modelValue.scriptIds.map((scriptId) => ({ id: createIdentity(), scriptId })));

watch(
  () => props.modelValue.scriptIds,
  (ids) => {
    if (ids.length === rows.value.length && ids.every((id, index) => id === rows.value[index]?.scriptId))
      return;
    const previous = [...rows.value];
    rows.value = ids.map((scriptId) => {
      const index = previous.findIndex((row) => row.scriptId === scriptId);
      return index >= 0 ? previous.splice(index, 1)[0]! : { id: createIdentity(), scriptId };
    });
  }
);

function publish(): void {
  const scriptIds = rows.value.map((row) => row.scriptId);
  const dependencies = props.modelValue.successDependencies
    ?.filter((item) => scriptIds.includes(item.scriptId))
    .map((item) => ({
      ...item,
      requiredScriptIds: item.requiredScriptIds.filter(
        (id) => scriptIds.indexOf(id) >= 0 && scriptIds.indexOf(id) < scriptIds.indexOf(item.scriptId)
      )
    }));
  emit('update:modelValue', {
    ...props.modelValue,
    scriptIds,
    ...(dependencies ? { successDependencies: dependencies } : {})
  });
}

function add(): void {
  rows.value.push({ id: createIdentity(), scriptId: '' });
  publish();
}

function choose(id: string, scriptId: string): void {
  const row = rows.value.find((item) => item.id === id);
  if (!row) return;
  row.scriptId = scriptId;
  publish();
}

function remove(id: string): void {
  rows.value = rows.value.filter((item) => item.id !== id);
  publish();
}

function reorder(ids: string[]): void {
  if (ids.length !== rows.value.length || new Set(ids).size !== ids.length) return;
  const next = ids.map((id) => rows.value.find((row) => row.id === id));
  if (next.some((row) => !row)) return;
  rows.value = next as typeof rows.value;
  publish();
}
</script>

<template>
  <section class="queue-section">
    <header>
      <h3>{{ t('任务列表') }}</h3>
      <small>{{ t('按顺序先后执行，拖拽左侧把手排序；长时运行与标准运行不能混合编排') }}</small>
    </header>
    <nexus-sortable-list :ids.prop="rows.map((row) => row.id)" @nexus-reorder="reorder($event.detail.ids)">
      <div v-for="(row, index) in rows" :key="row.id" :data-item-id="row.id" class="task-row">
        <nexus-drag-handle :item-id="row.id" :label="t('调整任务顺序')" />
        <nexus-select
          :aria-label="t('任务') + ' ' + (index + 1) + ' ' + t('脚本实例')"
          aria-required="true"
          :value.prop="row.scriptId"
          :placeholder="t('请选择脚本实例')"
          :options.prop="
            scripts.map((script) => ({
              id: script.id,
              label: script.name
            }))
          "
          @nexus-change="choose(row.id, $event.detail.value)"
        />
        <button type="button" class="danger" @click="remove(row.id)">{{ t('删除') }}</button>
      </div>
    </nexus-sortable-list>
    <button type="button" class="add-task" :disabled="rows.length >= 4096" @click="add">
      + {{ t('添加任务') }}
    </button>
  </section>
</template>

<style scoped>
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
.task-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 14px 8px 14px 0;
}
.task-row + .task-row {
  border-top: 1px solid var(--border);
}
nexus-drag-handle {
  flex: none;
  --nx-drag-size: 32px;
}
nexus-select {
  min-width: 0;
  flex: 1;
}
.task-row > button {
  min-width: 84px;
  font-size: 12px;
}
.add-task {
  margin-top: 12px;
  color: var(--muted);
}
@media (max-width: 600px) {
  header {
    align-items: start;
    flex-direction: column;
    gap: 4px;
  }
  .task-row {
    padding-right: 0;
  }
  .task-row > button {
    min-width: 64px;
    flex: none;
  }
}
</style>
