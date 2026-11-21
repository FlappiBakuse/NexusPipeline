<script setup lang="ts">
import { computed } from 'vue';

const props = defineProps<{ tasks: readonly { label: string; tone: string }[]; label: string }>();
const counts = computed(() => {
  const result = new Map<string, { label: string; tone: string; count: number }>();
  for (const { label, tone } of props.tasks) {
    const previous = result.get(label);
    if (previous) previous.count++;
    else result.set(label, { label, tone, count: 1 });
  }
  return [...result.values()];
});
</script>

<template>
  <ul v-if="counts.length" role="list" class="task-summary" :aria-label="label">
    <li v-for="item in counts" :key="item.label" :data-tone="item.tone">{{ item.label }} <strong>{{ item.count }}</strong></li>
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
li::before {
  content: '';
  display: inline-block;
  inline-size: 6px;
  block-size: 6px;
  margin-inline-end: 5px;
  border-radius: 50%;
  background: var(--muted);
  vertical-align: 1px;
}
li[data-tone='info']::before { background: var(--accent); }
li[data-tone='success']::before { background: var(--ok); }
li[data-tone='warning']::before { background: var(--warn); }
li[data-tone='danger']::before { background: var(--bad); }
strong { color: var(--text); font-variant-numeric: tabular-nums; }
</style>
