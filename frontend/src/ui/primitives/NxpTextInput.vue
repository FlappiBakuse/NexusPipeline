<script setup lang="ts">
const props = withDefaults(defineProps<{ modelValue?: string; placeholder?: string; disabled?: boolean; type?: string }>(), { modelValue: "", placeholder: "", disabled: false, type: "text" });
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();
function update(event: Event) { const value = (event.target as HTMLInputElement).value; emit("update:modelValue", value); }
function change(event: Event) { emit("change", (event.target as HTMLInputElement).value); }
</script>

<template><input class="nxp-input" :type="props.type" :value="props.modelValue" :placeholder="props.placeholder" :disabled="props.disabled" @input.stop="update" @change.stop="change" /></template>

<style>
.nxp-input { width: 100%; min-height: var(--nx-control-height); border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); padding: 0 var(--nx-space-3); background: var(--input-bg, transparent); color: var(--nx-color-text); font: inherit; }
.nxp-input:focus { border-color: var(--nx-color-accent); box-shadow: var(--nx-focus-ring); outline: 0; }
.nxp-input:disabled { opacity: .55; }
</style>
