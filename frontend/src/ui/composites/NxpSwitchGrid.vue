<script setup lang="ts">
const props = withDefaults(defineProps<{
  columns?: 1 | 2 | 3;
}>(), {
  columns: 3,
});
</script>

<template>
  <div class="nxp-switch-grid" :class="`columns-${props.columns}`"><slot /></div>
</template>

<style>
.nxp-switch-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: var(--space-3);
  margin: var(--space-3) 0;
  align-items: stretch;
}
.nxp-switch-grid.columns-1 { grid-template-columns: minmax(0, 1fr); }
.nxp-switch-grid.columns-2 { grid-template-columns: repeat(2, minmax(0, 1fr)); }
.nxp-switch-grid > .switch-card { height: 100%; min-height: 64px; }

@media (max-width: 820px) {
  .nxp-switch-grid,
  .nxp-switch-grid.columns-2,
  .nxp-switch-grid.columns-3 { grid-template-columns: minmax(0, 1fr); }
}
</style>
