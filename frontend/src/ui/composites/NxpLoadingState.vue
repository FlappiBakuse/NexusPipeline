<script setup lang="ts">
const props = withDefaults(
  defineProps<{
    title: string;
    description?: string;
    ariaLabel?: string;
    testId?: string;
  }>(),
  { description: "", ariaLabel: "", testId: "plugin-loading-state" },
);
</script>

<template>
  <div class="nxp-loading-state" :data-testid="props.testId" role="status" aria-live="polite" aria-busy="true">
    <div class="nxp-loading-progress" role="progressbar" :aria-label="props.ariaLabel || props.title" :aria-valuetext="props.title"><span /></div>
    <strong>{{ props.title }}</strong>
    <span v-if="props.description" class="muted">{{ props.description }}</span>
  </div>
</template>

<style>
:host { display: block; min-width: 0; }
.nxp-loading-state { display: grid; min-height: 176px; justify-items: center; align-content: center; gap: var(--space-2, var(--nx-space-2)); padding: var(--space-6, var(--nx-space-6)) var(--space-5, var(--nx-space-5)); text-align: center; }
.nxp-loading-state strong { font-size: 14px; }
.nxp-loading-state > .muted { margin: 0; }
.nxp-loading-progress { width: min(280px, 100%); height: 6px; overflow: hidden; border-radius: 999px; background: var(--content-card-soft, var(--nx-color-surface)); }
.nxp-loading-progress > span { display: block; width: 42%; height: 100%; border-radius: inherit; background: var(--accent, var(--nx-color-accent)); animation: nxp-loading-progress 1.4s ease-in-out infinite; }
@keyframes nxp-loading-progress { 0% { transform: translateX(-120%); } 50% { transform: translateX(100%); } 100% { transform: translateX(240%); } }
</style>
