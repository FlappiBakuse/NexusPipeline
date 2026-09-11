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
  <div class="plugin-loading-state" :data-testid="props.testId" role="status" aria-live="polite" aria-busy="true">
    <div class="plugin-loading-progress" role="progressbar" :aria-label="props.ariaLabel || props.title" :aria-valuetext="props.title"><span /></div>
    <strong>{{ props.title }}</strong>
    <span v-if="props.description" class="muted">{{ props.description }}</span>
  </div>
</template>
