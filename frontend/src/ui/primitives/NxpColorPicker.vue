<script setup lang="ts">
const props = withDefaults(defineProps<{ modelValue?: string; disabled?: boolean; ariaLabel?: string }>(), { modelValue: "#76a7ff", disabled: false, ariaLabel: "颜色" });
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();
function update(event: Event) { const value = (event.target as HTMLInputElement).value; emit("update:modelValue", value); emit("change", value); }
</script>

<template><input class="nxp-color" type="color" :value="props.modelValue" :disabled="props.disabled" :aria-label="props.ariaLabel" @input="update" @change="update" /></template>

<style>
.nxp-color { width: 52px; min-height: var(--nx-control-height); border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); padding: 3px; background: transparent; }
</style>
