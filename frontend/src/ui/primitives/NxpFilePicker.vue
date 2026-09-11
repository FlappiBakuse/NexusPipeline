<script setup lang="ts">
import { ref } from "vue";
import { t } from "../../platform/i18n";

const props = withDefaults(defineProps<{ accept?: string; multiple?: boolean; disabled?: boolean; label?: string }>(), { accept: "", multiple: false, disabled: false, label: "选择文件" });
const emit = defineEmits<{ change: [files: File[]] }>();
const input = ref<HTMLInputElement | null>(null);
const fileName = ref(t("common.no_file_selected"));
function changed(event: Event) {
  const files = Array.from((event.target as HTMLInputElement).files || []);
  fileName.value = files.length ? files.map(file => file.name).join(", ") : t("common.no_file_selected");
  emit("change", files);
}
</script>

<template>
  <span class="nxp-file"><button type="button" class="ghost nxp-file-trigger" :disabled="disabled" @click="input?.click()">{{ props.label }}</button><span class="nxp-file-name">{{ fileName }}</span><input ref="input" class="sr-only" type="file" :accept="accept" :multiple="multiple" :disabled="disabled" @change.stop="changed"></span>
</template>

<style>
:host {
  display: inline-flex;
  min-width: 0;
  vertical-align: middle;
}
.nxp-file { display: inline-flex; min-width: 0; align-items: center; flex-wrap: wrap; gap: var(--space-2, var(--nx-space-2)); }
.nxp-file-trigger { flex: 0 0 auto; min-height: var(--control-height, var(--nx-control-height)); padding-inline: var(--space-3, var(--nx-space-3)); border: 1px solid var(--content-control-border, var(--nx-color-border)); border-radius: var(--radius-sm, var(--nx-radius-sm)); background: var(--content-control, transparent); color: var(--nx-color-text); font: inherit; cursor: pointer; }
.nxp-file-trigger:hover:not(:disabled) { border-color: var(--accent, var(--nx-color-accent)); background: var(--content-control-hover, transparent); }
.nxp-file-trigger:disabled { cursor: not-allowed; opacity: .45; }
.nxp-file-name { min-width: 0; overflow: hidden; color: var(--muted, var(--nx-color-muted)); font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
</style>
