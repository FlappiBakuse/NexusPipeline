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
    class="drag-handle nxp-drag-handle"
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
  min-width: var(--nx-drag-handle-size, 32px);
  min-height: var(--nx-control-height, 36px);
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
.nxp-drag-handle:hover,
.nxp-drag-handle:focus-visible { border-color: var(--nx-color-border, currentColor); color: var(--nx-color-text, currentColor); }
.nxp-drag-handle:focus-visible { outline: none; box-shadow: var(--nx-focus-ring, 0 0 0 2px currentColor); }
.nxp-drag-handle:active { cursor: grabbing; }
.nxp-drag-handle:disabled { cursor: default; opacity: .45; }
</style>
