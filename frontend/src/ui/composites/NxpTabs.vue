<script setup lang="ts">
/** 标签页切换：保留 `role="tablist"` / `role="tab"` / `aria-selected` 语义，
 *  视觉沿用 shell 既有 `.plugin-tabs` 紧凑按钮组。 */
interface NxpTab {
  value: string;
  label: string;
  testId?: string;
  disabled?: boolean;
}

const props = withDefaults(defineProps<{
  modelValue: string;
  tabs: NxpTab[];
  ariaLabel?: string;
  testId?: string;
}>(), {
  ariaLabel: "",
  testId: undefined,
});

const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();

function select(tab: NxpTab) {
  if (tab.disabled || tab.value === props.modelValue) return;
  emit("update:modelValue", tab.value);
  emit("change", tab.value);
}
</script>

<template>
  <div class="plugin-tabs nxp-tabs" role="tablist" :aria-label="props.ariaLabel || undefined" :data-testid="props.testId">
    <button
      v-for="tab in props.tabs"
      :key="tab.value"
      class="nxp-tabs-tab"
      :class="tab.value === props.modelValue ? 'primary' : 'tertiary'"
      type="button"
      role="tab"
      :disabled="tab.disabled"
      :aria-selected="tab.value === props.modelValue"
      :data-testid="tab.testId"
      @click="select(tab)"
    >
      {{ tab.label }}
    </button>
  </div>
</template>

<style>
.nxp-tabs { display: inline-flex; flex-wrap: wrap; gap: var(--space-2); }
</style>
