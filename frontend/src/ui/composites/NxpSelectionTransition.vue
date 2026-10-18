<script setup lang="ts">
function activate(element: Element) { (element as HTMLElement).inert = false; }
function deactivate(element: Element) { (element as HTMLElement).inert = true; }
</script>

<template>
  <Transition name="nxp-selection" @before-enter="activate" @before-leave="deactivate"><slot /></Transition>
</template>

<style>
.nxp-selection-enter-active,
.nxp-selection-leave-active {
  transform-origin: var(--nx-overlay-origin, top left);
  transition: opacity var(--nx-motion-duration-fast, 160ms) var(--nx-motion-ease, ease), transform var(--nx-motion-duration-fast, 160ms) var(--nx-motion-ease, ease);
}
.nxp-selection-enter-from,
.nxp-selection-leave-to { opacity: 0; transform: scale(.97); }
.nxp-selection-leave-active { pointer-events: none; }
@media (prefers-reduced-motion: reduce) {
  .nxp-selection-enter-active,
  .nxp-selection-leave-active { transition: none; }
  .nxp-selection-enter-from,
  .nxp-selection-leave-to { transform: none; }
}
</style>
