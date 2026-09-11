<script setup lang="ts">
import { t } from "../../platform/i18n";
const props = withDefaults(
  defineProps<{
    modelValue?: boolean;
    label: string;
    description?: string;
    help?: string;
    disabled?: boolean;
    ariaLabel?: string;
  }>(),
  { modelValue: false, description: "", help: "", disabled: false, ariaLabel: "" },
);
const emit = defineEmits<{
  "update:modelValue": [value: boolean];
  change: [value: boolean];
}>();

function update(value: boolean) {
  emit("update:modelValue", value);
  emit("change", value);
}
</script>

<template>
  <div class="switch-row settings-option switch-card" :data-help="props.help || undefined">
    <div class="switch-copy">
      <strong>{{ props.label }}</strong>
      <span v-if="props.description" class="muted">{{ props.description }}</span>
    </div>
    <button
      class="mode-toggle switch-control nxp-switch"
      type="button"
      :disabled="props.disabled"
      :aria-disabled="props.disabled"
      :aria-pressed="props.modelValue"
      :aria-label="props.ariaLabel || props.label"
      :data-state="props.modelValue ? 'on' : 'off'"
      @click="update(!props.modelValue)"
    >
      <span class="switch-track" aria-hidden="true"><span class="switch-thumb" /></span>
      <span class="sr-only">{{ props.modelValue ? t("common.enabled") : t("common.disabled") }}</span>
    </button>
  </div>
</template>
