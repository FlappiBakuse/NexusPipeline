<script setup lang="ts">
defineProps<{ tasks: readonly { id: string; title: string; label: string; tone: string; reason?: string }[]; label: string }>();
</script>

<template>
  <div class="task-card-layout">
    <ol class="task-cards" role="list" :aria-label="label">
      <li v-for="(task, index) in tasks" :key="task.id" :data-task-id="task.id">
        <article class="task-card">
          <span class="task-number" :data-tone="task.tone" aria-hidden="true">{{ index + 1 }}</span>
          <strong>{{ task.title }}</strong>
          <nexus-badge :tone="task.tone" :label="task.label" />
          <p v-if="task.reason">{{ task.reason }}</p>
        </article>
      </li>
    </ol>
  </div>
</template>

<style scoped>
.task-card-layout { container: task-cards / inline-size; min-width: 0; }
.task-cards {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 9px;
  list-style: none;
  margin: 0;
  padding: 0;
}
@container task-cards (inline-size >= 32rem) {
  .task-cards { grid-template-columns: repeat(2, minmax(0, 1fr)); }
}
li { display: grid; min-width: 0; }
.task-card {
  display: grid;
  grid-template-columns: 28px minmax(0, 1fr) fit-content(44%);
  align-content: start;
  align-items: center;
  gap: 6px 9px;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: 11px;
  background: var(--content-control);
}
strong { font-size: 13px; overflow-wrap: anywhere; }
.task-number {
  display: grid;
  place-items: center;
  block-size: 28px;
  border-radius: 100px;
  background: var(--muted-soft);
  color: var(--muted);
  font-size: 12px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
}
.task-number[data-tone='info'] { background: var(--accent-soft); color: var(--accent); }
.task-number[data-tone='success'] { background: var(--ok-soft); color: var(--ok); }
.task-number[data-tone='danger'] { background: var(--bad-soft); color: var(--bad); }
.task-number[data-tone='warning'] { background: var(--warn-soft); color: var(--warn); }
nexus-badge { justify-self: end; min-inline-size: 0; }
p { grid-column: 2 / -1; margin: 0; color: var(--muted); font-size: 12px; overflow-wrap: anywhere; }
</style>
