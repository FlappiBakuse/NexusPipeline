<script setup lang="ts">
import { computed } from 'vue';

const props = defineProps<{ tasks: readonly { label: string }[]; label: string }>();
const counts = computed(() => {
  const result = new Map<string, number>();
  for (const { label } of props.tasks) result.set(label, (result.get(label) || 0) + 1);
  return [...result];
});
</script>

<template>
  <ul v-if="counts.length" role="list" class="task-summary" :aria-label="label">
    <li v-for="[name, count] in counts" :key="name">{{ name }} <strong>{{ count }}</strong></li>
  </ul>
</template>

<style scoped>
.task-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  margin: 0;
  padding: 0;
  color: var(--muted);
  font-size: 12px;
  list-style: none;
}
strong { color: var(--text); font-variant-numeric: tabular-nums; }
</style>
