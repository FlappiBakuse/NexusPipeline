<script setup lang="ts">
import { fieldHelp as vHelp } from '../components/field-help';
import { createIdentity } from '../control/identity';
import { computed, ref, watch } from 'vue';
import type { AutomationBundle } from '../control/wire.generated';
import { t } from '../app/locale';

type Queue = AutomationBundle['queues'][number];
const props = defineProps<{ modelValue: Queue; scripts: AutomationBundle['scripts'] }>();
const emit = defineEmits<{ 'update:modelValue': [value: Queue] }>();
const rows = ref(props.modelValue.scriptIds.map((scriptId) => ({ id: createIdentity(), scriptId })));
const scriptOptions = computed(() => props.scripts.map(script => ({ id: script.id, label: script.name })));

watch(
  () => props.modelValue.scriptIds,
  (ids) => {
    if (ids.length === rows.value.length && ids.every((id, index) => id === rows.value[index]?.scriptId))
      return;
    const previous = new Map<string, typeof rows.value>();
    for (let index = rows.value.length - 1; index >= 0; index--) {
      const row = rows.value[index]!;
      const matching = previous.get(row.scriptId);
      if (matching) matching.push(row);
      else previous.set(row.scriptId, [row]);
    }
    rows.value = ids.map(scriptId => previous.get(scriptId)?.pop() ?? { id: createIdentity(), scriptId });
  }
);

function publish(): void {
  const scriptIds = rows.value.map((row) => row.scriptId);
  const positions = new Map<string, number>();
  scriptIds.forEach((id, index) => { if (!positions.has(id)) positions.set(id, index); });
  const dependencies = props.modelValue.successDependencies
    ?.filter((item) => positions.has(item.scriptId))
    .map((item) => ({
      ...item,
      requiredScriptIds: item.requiredScriptIds.filter(
        (id) => (positions.get(id) ?? Infinity) < positions.get(item.scriptId)!
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
  const indexed = new Map(rows.value.map(row => [row.id, row]));
  const next = ids.map(id => indexed.get(id));
  if (next.some((row) => !row)) return;
  rows.value = next as typeof rows.value;
  publish();
}
</script>

<template>
  <section class="queue-section">
    <header>
      <h3 tabindex="0" v-help="t('按顺序先后执行，拖拽左侧把手排序；长时运行与标准运行不能混合编排')">{{ t('任务列表') }}</h3>
    </header>
    <nexus-sortable-list :ids.prop="rows.map((row) => row.id)" @nexus-reorder="reorder($event.detail.ids)">
      <div v-for="(row, index) in rows" :key="row.id" :data-item-id="row.id" class="task-row">
        <nexus-drag-handle :item-id="row.id" :label="t('调整任务顺序')" />
        <nexus-select
        data-draft-field="QueueTasks/row.scriptId"
          :aria-label="t('任务') + ' ' + (index + 1) + ' ' + t('脚本实例')"
          aria-required="true"
          :value.prop="row.scriptId"
          :placeholder="t('请选择脚本实例')"
          :options.prop="scriptOptions"
          @nexus-change="choose(row.id, $event.detail.value)"
        />
        <div class="entity-card-actions">
          <nexus-icon-button icon="trash" class="danger-action" :label="t('删除任务')" @click="remove(row.id)" />
        </div>
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
  gap: var(--queue-row-gap);
  padding-block: 14px;
}
.task-row + .task-row {
  border-top: 1px solid var(--border);
}
nexus-drag-handle {
  flex: none;
}
nexus-select {
  min-width: 0;
  flex: 1;
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
}
</style>
