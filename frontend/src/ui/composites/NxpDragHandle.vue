<script setup lang="ts">
const props = withDefaults(defineProps<{
  label?: string;
  title?: string;
  disabled?: boolean;
  tabIndex?: number;
  ariaHidden?: boolean;
}>(), {
  label: "Reorder",
  title: "",
  disabled: false,
  tabIndex: 0,
  ariaHidden: false,
});
</script>

<template>
  <button
    class="nxp-drag-handle"
    type="button"
    :tabindex="props.tabIndex"
    :aria-label="props.label"
    :title="props.title || undefined"
    :aria-disabled="props.disabled ? 'true' : 'false'"
    :aria-hidden="props.ariaHidden ? 'true' : undefined"
    :disabled="props.disabled"
    data-drag-handle
    @click.stop.prevent
  >⠿</button>
</template>

<style>
.nxp-drag-handle {
  display: inline-flex;
  flex: 0 0 var(--nx-control-height, 40px);
  width: var(--nx-control-height, 40px);
  min-width: var(--nx-control-height, 40px);
  min-height: var(--nx-control-height, 40px);
  margin-inline: calc((var(--nx-drag-handle-size, 24px) - var(--nx-control-height, 40px)) / 2);
  align-items: center;
  justify-content: center;
  padding: 0;
  border: 1px solid transparent;
  border-radius: var(--nx-radius-sm, 6px);
  background: transparent;
  color: var(--nx-color-muted, currentColor);
  cursor: grab;
  user-select: none;
  -webkit-user-select: none;
  touch-action: none;
}
.nxp-drag-handle:hover { border-color: transparent; background: transparent; color: var(--nx-color-muted, currentColor); }
.nxp-drag-handle:focus-visible { outline: none; box-shadow: var(--nx-focus-ring, 0 0 0 2px currentColor); }
.nxp-drag-handle:active { cursor: grabbing; }
.nxp-drag-handle:disabled { cursor: default; opacity: .45; }
</style>
