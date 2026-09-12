<script setup lang="ts">
import NxpSwitch from "../primitives/NxpSwitch.vue";
const props = withDefaults(
  defineProps<{
    id?: string;
    modelValue?: boolean;
    label: string;
    description?: string;
    help?: string;
    disabled?: boolean;
    ariaLabel?: string;
  }>(),
  { id: "", modelValue: false, description: "", help: "", disabled: false, ariaLabel: "" },
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
    <NxpSwitch
      :id="props.id || undefined"
      :model-value="props.modelValue"
      :disabled="props.disabled"
      :aria-label="props.ariaLabel || props.label"
      @update:model-value="update"
    />
  </div>
</template>
