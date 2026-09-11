<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from "vue";
import { bindFloatingReposition, positionFloatingOverlay } from "../floating";

const props = withDefaults(defineProps<{ open?: boolean; label?: string; disabled?: boolean }>(), { open: false, label: "菜单", disabled: false });
const emit = defineEmits<{ "update:open": [value: boolean]; close: [] }>();
const expanded = ref(props.open);
const root = ref<HTMLElement | null>(null);
const trigger = ref<HTMLButtonElement | null>(null);
const popup = ref<HTMLElement | null>(null);
const popupId = `nxp-menu-popup-${Math.random().toString(36).slice(2)}`;
let unbindReposition: (() => void) | null = null;
watch(() => props.open, value => { expanded.value = value; });
function reposition() { if (expanded.value && trigger.value && popup.value) positionFloatingOverlay(trigger.value, popup.value, { minWidth: 160 }); }
async function setOpen(value: boolean, restoreFocus = true) {
  if (props.disabled) return;
  expanded.value = value;
  emit("update:open", value);
  if (!value) {
    unbindReposition?.();
    unbindReposition = null;
    if (restoreFocus) {
      await nextTick();
      trigger.value?.focus();
    }
    return;
  }
  document.dispatchEvent(new CustomEvent("nxp-overlay-open", { detail: popupId }));
  await nextTick();
  reposition();
  unbindReposition?.();
  unbindReposition = bindFloatingReposition(reposition);
}
function toggle() { void setOpen(!expanded.value); }
function close() { void setOpen(false); emit("close"); }
function closeOutside(event: PointerEvent) {
  const target = event.target;
  if (!expanded.value || !(target instanceof Node) || root.value?.contains(target) || popup.value?.contains(target)) return;
  void setOpen(false, false);
}
function closeOther(event: Event) { if ((event as CustomEvent<string>).detail !== popupId) void setOpen(false, false); }
function closeEscape(event: KeyboardEvent) {
  if (event.key !== "Escape" || !expanded.value) return;
  event.preventDefault();
  event.stopPropagation();
  close();
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
  <div ref="root" class="nxp-menu">
    <button ref="trigger" class="nxp-menu-trigger" type="button" :aria-label="props.label" :aria-expanded="expanded" :aria-controls="popupId" :disabled="props.disabled" @click="toggle"><slot name="trigger">{{ props.label }}</slot></button>
    <Teleport to="body">
      <div v-if="expanded" ref="popup" :id="popupId" class="nxp-menu-popup" role="menu" @click="close"><slot /></div>
    </Teleport>
  </div>
</template>

<style>
.nxp-menu-popup { position: fixed !important; z-index: 1000; }
</style>
