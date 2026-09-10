<script setup lang="ts">
const props = withDefaults(defineProps<{ modelValue?: string; placeholder?: string; disabled?: boolean; rows?: number }>(), { modelValue: "", placeholder: "", disabled: false, rows: 4 });
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();
function update(event: Event) { emit("update:modelValue", (event.target as HTMLTextAreaElement).value); }
function change(event: Event) { emit("change", (event.target as HTMLTextAreaElement).value); }
</script>

<template><textarea class="nxp-input nxp-textarea" :value="props.modelValue" :placeholder="props.placeholder" :disabled="props.disabled" :rows="props.rows" @input="update" @change="change" /></template>

<style>
.nxp-textarea { min-height: 96px; padding-block: var(--nx-space-3); resize: vertical; }
</style>
