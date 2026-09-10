<script setup lang="ts">
withDefaults(defineProps<{ accept?: string; multiple?: boolean; disabled?: boolean; label?: string }>(), { accept: "", multiple: false, disabled: false, label: "选择文件" });
const emit = defineEmits<{ change: [files: File[]] }>();
function changed(event: Event) { emit("change", Array.from((event.target as HTMLInputElement).files || [])); }
</script>

<template><span class="nxp-file"><input class="nxp-file-input" type="file" :accept="accept" :multiple="multiple" :disabled="disabled" @change="changed" /><span class="nxp-file-label">{{ label }}</span></span></template>

<style>
.nxp-file { display: inline-flex; min-height: var(--nx-control-height); align-items: center; position: relative; overflow: hidden; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); padding: 0 var(--nx-space-3); color: var(--nx-color-text); cursor: pointer; }
.nxp-file-input { position: absolute; inset: 0; width: 100%; height: 100%; opacity: 0; cursor: pointer; }
</style>
