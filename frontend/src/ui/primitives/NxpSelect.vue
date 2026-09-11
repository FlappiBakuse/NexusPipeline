<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref } from "vue";
import { t } from "../../platform/i18n";
import { bindFloatingReposition, positionFloatingOverlay } from "../floating";

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
const menu = ref<HTMLElement | null>(null);
const activeIndex = ref(-1);
const menuId = props.id ? `${props.id}-menu` : `nxp-select-menu-${Math.random().toString(36).slice(2)}`;
let unbindReposition: (() => void) | null = null;

const selected = () => Array.isArray(props.modelValue)
  ? props.modelValue.map(String)
  : String(props.modelValue ?? "").length ? [String(props.modelValue)] : [];
const selectedOptions = () => props.options.filter(option => selected().includes(String(option.value)));
const summary = () => {
  const values = selectedOptions();
  if (values.length) return values.map(option => option.label).join(", ");
  if (!props.multiple) {
    const emptyOption = props.options.find(option => String(option.value) === "");
    if (emptyOption) return emptyOption.label;
  }
  return props.placeholder || t("common.select_an_option");
};
const serialized = () => props.multiple ? JSON.stringify(selected()) : selected()[0] || "";

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
  menu.value?.querySelector<HTMLButtonElement>(`[data-option-index="${activeIndex.value}"]`)?.focus();
}
function reposition() {
  if (open.value && trigger.value && menu.value) positionFloatingOverlay(trigger.value, menu.value);
}
async function setOpen(value: boolean, restoreFocus = true) {
  if (props.disabled) return;
  open.value = value;
  if (!value) {
    activeIndex.value = -1;
    unbindReposition?.();
    unbindReposition = null;
    if (restoreFocus) {
      await nextTick();
      trigger.value?.focus();
    }
    return;
  }
  document.dispatchEvent(new CustomEvent("nxp-overlay-open", { detail: menuId }));
  const currentIndex = props.options.findIndex(option => selected().includes(String(option.value)) && !option.disabled);
  activeIndex.value = currentIndex >= 0 ? currentIndex : nextSelectable(-1, 1);
  await nextTick();
  reposition();
  unbindReposition?.();
  unbindReposition = bindFloatingReposition(reposition);
  focusActive();
}
function choose(option: NxpOption) {
  if (option.disabled) return;
  const value = String(option.value);
  if (props.multiple) {
    const current = selected();
    emitValue(current.includes(value) ? current.filter(item => item !== value) : [...current, value]);
    return;
  }
  emitValue(value);
  void setOpen(false);
}
function onTriggerKeydown(event: KeyboardEvent) {
  if (["ArrowDown", "ArrowUp", "Enter", " "].includes(event.key)) {
    event.preventDefault();
    if (!open.value) void setOpen(true);
    else if (event.key === "ArrowDown") { activeIndex.value = nextSelectable(activeIndex.value, 1); focusActive(); }
    else if (event.key === "ArrowUp") { activeIndex.value = nextSelectable(activeIndex.value, -1); focusActive(); }
    else if (activeIndex.value >= 0) choose(props.options[activeIndex.value]);
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
  const target = event.target;
  if (!open.value || !(target instanceof Node) || root.value?.contains(target) || menu.value?.contains(target)) return;
  void setOpen(false, false);
}
function closeOther(event: Event) {
  if ((event as CustomEvent<string>).detail !== menuId) void setOpen(false, false);
}
function closeEscape(event: KeyboardEvent) {
  if (event.key !== "Escape" || !open.value) return;
  event.preventDefault();
  event.stopPropagation();
  void setOpen(false);
}

if (typeof document !== "undefined") {
  document.addEventListener("pointerdown", closeOutside);
  document.addEventListener("nxp-overlay-open", closeOther);
  document.addEventListener("keydown", closeEscape, true);
}
onBeforeUnmount(() => {
  unbindReposition?.();
  document.removeEventListener("pointerdown", closeOutside);
  document.removeEventListener("nxp-overlay-open", closeOther);
  document.removeEventListener("keydown", closeEscape, true);
});
</script>

<template>
  <div ref="root" class="nxp-select">
    <input :id="props.id || undefined" type="hidden" :value="serialized()" data-nxp-select-value :data-nxp-select-multiple="multiple ? 'true' : undefined">
    <button ref="trigger" :id="props.id ? `${props.id}-trigger` : undefined" class="nxp-select-trigger" type="button" :disabled="disabled" :aria-label="ariaLabel || summary()" aria-haspopup="listbox" :aria-expanded="open" :aria-controls="menuId" @click="setOpen(!open)" @keydown="onTriggerKeydown">
      <span data-nxp-select-label>{{ summary() }}</span><span class="nxp-select-chevron" aria-hidden="true">⌄</span>
    </button>
    <Teleport to="body">
      <div ref="menu" v-show="open" :id="menuId" class="nxp-select-menu secondary-surface" role="listbox" :aria-multiselectable="multiple || undefined">
        <button v-for="(option, index) in options" :key="option.value" class="nxp-select-option" :class="{ 'is-selected': selected().includes(String(option.value)) }" type="button" role="option" data-nxp-select-option :data-value="option.value" :data-option-index="index" :disabled="option.disabled" :aria-selected="selected().includes(String(option.value))" :title="option.title || undefined" @click="choose(option)" @keydown="onOptionKeydown($event, index)">
          <span>{{ option.label }}</span><span v-if="selected().includes(String(option.value))" class="nxp-select-check" aria-hidden="true">✓</span>
        </button>
      </div>
    </Teleport>
  </div>
</template>

<style>
:host {
  display: block;
  min-width: 0;
  width: 100%;
}
.nxp-select-menu { position: fixed !important; z-index: 1000; }
</style>
