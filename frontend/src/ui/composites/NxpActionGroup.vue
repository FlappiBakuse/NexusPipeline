<script setup lang="ts">
const props = withDefaults(defineProps<{
  direction?: "row" | "column";
  align?: "start" | "center" | "end" | "stretch";
  justify?: "start" | "center" | "end" | "between";
}>(), {
  direction: "row",
  align: "center",
  justify: "end",
});
</script>

<template>
  <div
    class="nxp-action-group"
    :class="[
      `is-${props.direction}`,
      `align-${props.align}`,
      `justify-${props.justify}`,
    ]"
  >
    <slot />
  </div>
</template>

<style>
.nxp-action-group { display: flex; min-width: 0; flex-wrap: wrap; gap: var(--nx-action-gap, var(--action-gap, 8px)); }
.nxp-action-group.is-column { flex-direction: column; }
.nxp-action-group.align-start { align-items: flex-start; }
.nxp-action-group.align-center { align-items: center; }
.nxp-action-group.align-end { align-items: flex-end; }
.nxp-action-group.align-stretch { align-items: stretch; }
.nxp-action-group.justify-start { justify-content: flex-start; }
.nxp-action-group.justify-center { justify-content: center; }
.nxp-action-group.justify-end { justify-content: flex-end; }
.nxp-action-group.justify-between { justify-content: space-between; }
</style>
