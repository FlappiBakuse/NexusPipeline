<script setup lang="ts">
const props = withDefaults(defineProps<{
  id?: string;
  modelValue?: string;
  placeholder?: string;
  disabled?: boolean;
  rows?: number;
  maxlength?: number | string;
  readonly?: boolean;
  ariaLabel?: string;
}>(), {
  id: undefined,
  modelValue: "",
  placeholder: "",
  disabled: false,
  rows: 4,
  maxlength: undefined,
  readonly: false,
  ariaLabel: undefined,
});
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();
function update(event: Event) { emit("update:modelValue", (event.target as HTMLTextAreaElement).value); }
function change(event: Event) { emit("change", (event.target as HTMLTextAreaElement).value); }
</script>

<template><textarea class="nxp-input nxp-textarea" :id="props.id" :value="props.modelValue" :placeholder="props.placeholder" :disabled="props.disabled" :rows="props.rows" :maxlength="props.maxlength" :readonly="props.readonly" :aria-label="props.ariaLabel" @input.stop="update" @change.stop="change" /></template>

<style>
.nxp-textarea { min-height: 96px; padding-block: var(--nx-space-3); resize: vertical; }
</style>
