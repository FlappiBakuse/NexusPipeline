<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { t } from "../../platform/i18n";

const props = withDefaults(defineProps<{ modelValue?: string; disabled?: boolean; ariaLabel?: string }>(), { modelValue: "#76a7ff", disabled: false, ariaLabel: "颜色" });
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();
const picker = ref<HTMLInputElement | null>(null);
const text = ref(props.modelValue);
watch(() => props.modelValue, next => { text.value = next; });
const normalized = computed(() => /^#[0-9a-f]{6}$/i.test(text.value) ? text.value.toLowerCase() : /^#[0-9a-f]{3}$/i.test(text.value) ? `#${text.value.slice(1).split("").map(part => `${part}${part}`).join("")}`.toLowerCase() : "#000000");
function update(value: string) {
  text.value = value;
  emit("update:modelValue", value);
  emit("change", value);
}
function updateText(event: Event) { update((event.target as HTMLInputElement).value); }
function updatePicker(event: Event) { update((event.target as HTMLInputElement).value); }
</script>

<template>
  <div class="nxp-color"><div class="nxp-color-row"><input class="nxp-color-value" type="text" inputmode="text" :value="text" :disabled="disabled" :aria-label="ariaLabel" @input.stop="updateText" @change.stop="updateText"><button class="nxp-color-trigger" type="button" :disabled="disabled" :aria-label="t('common.open_color_picker')" @click="picker?.click()"><span class="nxp-color-swatch" :style="{ backgroundColor: normalized }" aria-hidden="true" /><span>{{ t("common.select_color") }}</span></button></div><input ref="picker" class="sr-only" type="color" :value="normalized" :disabled="disabled" @input.stop="updatePicker" @change.stop="updatePicker"></div>
</template>

<style>
.nxp-color { min-width: 0; }
.nxp-color-row { display: flex; min-width: 0; align-items: stretch; }
.nxp-color-value { flex: 1 1 auto; min-width: 0; height: 40px; padding: 0 12px; border: 1px solid var(--content-control-border, var(--nx-color-border)); border-radius: 8px 0 0 8px; outline: none; background: var(--content-control, transparent); color: var(--nx-color-text); font: inherit; }
.nxp-color-value:focus { border-color: var(--accent, var(--nx-color-accent)); }
.nxp-color-trigger { display: inline-flex; min-width: 112px; min-height: 40px; align-items: center; gap: 7px; padding: 0 10px; border-radius: 0 8px 8px 0; color: var(--muted, var(--nx-color-muted)); font: inherit; font-size: 12px; }
.nxp-color-trigger:hover:not(:disabled) { border-color: var(--accent, var(--nx-color-accent)); background: var(--content-control-hover, transparent); color: var(--nx-color-text); }
.nxp-color-swatch { width: 18px; height: 18px; flex: 0 0 auto; border: 1px solid var(--content-control-border, var(--nx-color-border)); border-radius: 50%; box-shadow: inset 0 0 0 2px var(--content-card, var(--nx-color-surface)); }
</style>
