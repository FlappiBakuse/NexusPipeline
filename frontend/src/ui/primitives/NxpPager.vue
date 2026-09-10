<script setup lang="ts">
const props = withDefaults(defineProps<{ page?: number; totalPages?: number; total?: number; label?: string }>(), { page: 1, totalPages: 1, total: 0, label: "分页" });
const emit = defineEmits<{ pageChange: [page: number]; "update:page": [page: number] }>();

function go(page: number) {
  const next = Math.max(1, Math.min(props.totalPages, page));
  if (next === props.page) return;
  emit("update:page", next);
  emit("pageChange", next);
}
</script>

<template>
  <nav class="nxp-pager" :aria-label="props.label">
    <button type="button" :disabled="props.page <= 1" @click="go(props.page - 1)">‹</button>
    <span aria-live="polite">{{ props.page }} / {{ props.totalPages }}<small v-if="props.total"> · {{ props.total }}</small></span>
    <button type="button" :disabled="props.page >= props.totalPages" @click="go(props.page + 1)">›</button>
  </nav>
</template>

<style>
.nxp-pager { display: inline-flex; min-height: var(--nx-control-height); align-items: center; gap: var(--nx-space-2); color: var(--nx-color-muted); font-size: 12px; }
.nxp-pager button { min-width: 36px; min-height: 36px; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); background: transparent; color: var(--nx-color-text); font: inherit; cursor: pointer; }
.nxp-pager button:disabled { opacity: .45; cursor: not-allowed; }
.nxp-pager small { color: var(--nx-color-muted); }
</style>
