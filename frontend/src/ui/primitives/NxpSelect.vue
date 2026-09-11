<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref } from "vue";
import { t } from "@legacy/core/i18n.js";

export interface NxpOption { value: string; label: string; disabled?: boolean; title?: string }

const props = withDefaults(defineProps<{
  id?: string;
  modelValue?: string | string[];
  options?: NxpOption[];
  multiple?: boolean;
  disabled?: boolean;
  ariaLabel?: string;
  placeholder?: string;
}>(), { id: "", modelValue: "", options: () => [], multiple: false, disabled: false, ariaLabel: "" });
const emit = defineEmits<{ "update:modelValue": [value: string | string[]]; change: [value: string | string[]] }>();
const open = ref(false);
const root = ref<HTMLElement | null>(null);
const trigger = ref<HTMLButtonElement | null>(null);
const activeIndex = ref(-1);

const selected = computed(() => {
  if (Array.isArray(props.modelValue)) return props.modelValue.map(String);
  return String(props.modelValue ?? "").length ? [String(props.modelValue)] : [];
});
const selectedOptions = computed(() => props.options.filter(option => selected.value.includes(String(option.value))));
const summary = computed(() => {
  if (selectedOptions.value.length) return selectedOptions.value.map(option => option.label).join(", ");
  if (!props.multiple) {
    const emptyOption = props.options.find(option => String(option.value) === "");
    if (emptyOption) return emptyOption.label;
  }
  return props.placeholder || t("common.select_an_option");
});
const serialized = computed(() => props.multiple ? JSON.stringify(selected.value) : selected.value[0] || "");

function emitValue(value: string | string[]) {
  emit("update:modelValue", value);
  emit("change", value);
}
function isSelectable(index: number) { return Boolean(props.options[index]) && !props.options[index].disabled; }
function nextSelectable(start: number, direction: 1 | -1) {
  if (!props.options.length) return -1;
  let index = start;
  for (let count = 0; count < props.options.length; count += 1) {
    index = (index + direction + props.options.length) % props.options.length;
    if (isSelectable(index)) return index;
  }
  return -1;
}
function focusActive() {
  if (activeIndex.value < 0) return;
  root.value?.querySelector<HTMLButtonElement>(`[data-option-index="${activeIndex.value}"]`)?.focus();
}
async function setOpen(value: boolean) {
  if (props.disabled) return;
  open.value = value;
  if (!value) {
    activeIndex.value = -1;
    await nextTick();
    trigger.value?.focus();
    return;
  }
  const currentIndex = props.options.findIndex(option => selected.value.includes(String(option.value)) && !option.disabled);
  activeIndex.value = currentIndex >= 0 ? currentIndex : nextSelectable(-1, 1);
  await nextTick();
  focusActive();
}
function choose(option: NxpOption) {
  if (option.disabled) return;
  const value = String(option.value);
  if (props.multiple) {
    const values = selected.value.includes(value) ? selected.value.filter(item => item !== value) : [...selected.value, value];
    emitValue(values);
    return;
  }
  emitValue(value);
  void setOpen(false);
}
function onTriggerKeydown(event: KeyboardEvent) {
  if (event.key === "ArrowDown" || event.key === "ArrowUp" || event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    if (!open.value) void setOpen(true);
    else if (event.key === "ArrowDown") { activeIndex.value = nextSelectable(activeIndex.value, 1); focusActive(); }
    else if (event.key === "ArrowUp") { activeIndex.value = nextSelectable(activeIndex.value, -1); focusActive(); }
    else if (activeIndex.value >= 0) choose(props.options[activeIndex.value]);
  } else if (event.key === "Escape" && open.value) {
    event.preventDefault();
    void setOpen(false);
  }
}
function onOptionKeydown(event: KeyboardEvent, index: number) {
  if (event.key === "ArrowDown" || event.key === "ArrowUp") {
    event.preventDefault();
    activeIndex.value = nextSelectable(index, event.key === "ArrowDown" ? 1 : -1);
    focusActive();
  } else if (event.key === "Escape") {
    event.preventDefault();
    void setOpen(false);
  } else if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    choose(props.options[index]);
  }
}
function closeOutside(event: PointerEvent) {
  if (open.value && root.value && event.target instanceof Node && !root.value.contains(event.target)) void setOpen(false);
}

if (typeof document !== "undefined") document.addEventListener("pointerdown", closeOutside);
onBeforeUnmount(() => document.removeEventListener("pointerdown", closeOutside));
</script>

<template>
  <div ref="root" class="nxp-select">
    <input :id="props.id || undefined" type="hidden" :value="serialized" data-nxp-select-value>
    <button ref="trigger" :id="props.id ? `${props.id}-trigger` : undefined" class="nxp-select-trigger" type="button" :disabled="disabled" :aria-label="ariaLabel || summary" aria-haspopup="listbox" :aria-expanded="open" :aria-controls="props.id ? `${props.id}-menu` : undefined" @click="setOpen(!open)" @keydown="onTriggerKeydown">
      <span data-nxp-select-label>{{ summary }}</span><span class="nxp-select-chevron" aria-hidden="true">⌄</span>
    </button>
    <div v-show="open" :id="props.id ? `${props.id}-menu` : undefined" class="nxp-select-menu secondary-surface" role="listbox" :aria-multiselectable="multiple || undefined">
      <button v-for="(option, index) in options" :key="option.value" class="nxp-select-option" :class="{ 'is-selected': selected.includes(String(option.value)) }" type="button" role="option" data-nxp-select-option :data-value="option.value" :data-option-index="index" :disabled="option.disabled" :aria-selected="selected.includes(String(option.value))" :title="option.title || undefined" @click="choose(option)" @keydown="onOptionKeydown($event, index)">
        <span>{{ option.label }}</span><span v-if="selected.includes(String(option.value))" class="nxp-select-check" aria-hidden="true">✓</span>
      </button>
    </div>
  </div>
</template>
