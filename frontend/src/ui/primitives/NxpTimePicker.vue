<script setup lang="ts">
const props = withDefaults(defineProps<{ modelValue?: string; disabled?: boolean; ariaLabel?: string }>(), { modelValue: "", disabled: false, ariaLabel: "时间" });
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();
function update(event: Event) { const value = (event.target as HTMLInputElement).value; emit("update:modelValue", value); emit("change", value); }
</script>

<template><input class="nxp-input" type="time" :value="props.modelValue" :disabled="props.disabled" :aria-label="props.ariaLabel" @input="update" @change="update" /></template>
