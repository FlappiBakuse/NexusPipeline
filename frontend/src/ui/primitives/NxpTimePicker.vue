<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from "vue";
import { bindFloatingReposition, positionFloatingOverlay } from "../floating";

const props = withDefaults(defineProps<{ id?: string; modelValue?: string; disabled?: boolean; ariaLabel?: string }>(), { id: "", modelValue: "", disabled: false, ariaLabel: "时间" });
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();
const open = ref(false);
const root = ref<HTMLElement | null>(null);
const anchor = ref<HTMLElement | null>(null);
const popover = ref<HTMLElement | null>(null);
const [initialHour, initialMinute] = String(props.modelValue || "").split(":");
const hour = ref(Number.isFinite(Number(initialHour)) ? Number(initialHour) % 24 : 0);
const minute = ref(Number.isFinite(Number(initialMinute)) ? Number(initialMinute) % 60 : 0);
const popoverId = props.id ? `${props.id}-popover` : `nxp-time-popover-${Math.random().toString(36).slice(2)}`;
let unbindReposition: (() => void) | null = null;

watch(() => props.modelValue, next => {
  const [nextHour, nextMinute] = String(next || "").split(":");
  if (nextHour !== undefined && Number.isFinite(Number(nextHour))) hour.value = Number(nextHour) % 24;
  if (nextMinute !== undefined && Number.isFinite(Number(nextMinute))) minute.value = Number(nextMinute) % 60;
});
function text(value: number) { return String(value).padStart(2, "0"); }
function value() { return `${text(hour.value)}:${text(minute.value)}`; }
function commit() { const next = value(); emit("update:modelValue", next); emit("change", next); }
function adjust(unit: "hour" | "minute", delta: number) {
  if (unit === "hour") hour.value = (hour.value + delta + 24) % 24;
  else minute.value = (minute.value + delta + 60) % 60;
  commit();
}
function choose(unit: "hour" | "minute", next: number) {
  if (unit === "hour") hour.value = next;
  else minute.value = next;
  commit();
}
function reposition() { if (open.value && anchor.value && popover.value) positionFloatingOverlay(anchor.value, popover.value); }
async function setOpen(value: boolean, restoreFocus = true) {
  if (props.disabled) return;
  open.value = value;
  if (!value) {
    unbindReposition?.();
    unbindReposition = null;
    if (restoreFocus) {
      await nextTick();
      anchor.value?.querySelector<HTMLInputElement>(".nxp-time-value")?.focus();
    }
    return;
  }
  document.dispatchEvent(new CustomEvent("nxp-overlay-open", { detail: popoverId }));
  await nextTick();
  reposition();
  unbindReposition?.();
  unbindReposition = bindFloatingReposition(reposition);
}
function toggle() { void setOpen(!open.value); }
function closeOutside(event: PointerEvent) {
  const target = event.target;
  if (!open.value || !(target instanceof Node) || root.value?.contains(target) || popover.value?.contains(target)) return;
  void setOpen(false, false);
}
function closeOther(event: Event) {
  if ((event as CustomEvent<string>).detail === popoverId) return;
  void setOpen(false, false);
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
  <div ref="root" class="nxp-time">
    <div ref="anchor" class="nxp-time-input-wrap"><input :id="props.id || undefined" class="nxp-time-value" type="text" readonly :value="value()" :disabled="disabled" :aria-label="ariaLabel" aria-haspopup="dialog" :aria-expanded="open" :aria-controls="popoverId" @click="toggle"><button class="nxp-time-trigger" type="button" :disabled="disabled" :aria-label="`${ariaLabel}选择`" :aria-expanded="open" @click="toggle">⌄</button></div>
    <Teleport to="body">
      <div ref="popover" v-show="open" :id="popoverId" class="nxp-time-popover secondary-surface" role="dialog" :aria-label="ariaLabel">
        <div class="nxp-time-columns">
          <div class="nxp-time-wheel" role="group" aria-label="小时"><button class="nxp-time-step" type="button" aria-label="减少小时" @click="adjust('hour', -1)">⌃</button><div class="nxp-time-viewport" role="listbox" aria-label="小时"><button v-for="offset in [-1, 0, 1]" :key="`h-${offset}`" class="nxp-time-option" :aria-selected="offset === 0" type="button" @click="choose('hour', (hour + offset + 24) % 24)">{{ text((hour + offset + 24) % 24) }}</button></div><button class="nxp-time-step" type="button" aria-label="增加小时" @click="adjust('hour', 1)">⌄</button></div>
          <div class="nxp-time-wheel" role="group" aria-label="分钟"><button class="nxp-time-step" type="button" aria-label="减少分钟" @click="adjust('minute', -1)">⌃</button><div class="nxp-time-viewport" role="listbox" aria-label="分钟"><button v-for="offset in [-1, 0, 1]" :key="`m-${offset}`" class="nxp-time-option" :aria-selected="offset === 0" type="button" @click="choose('minute', (minute + offset + 60) % 60)">{{ text((minute + offset + 60) % 60) }}</button></div><button class="nxp-time-step" type="button" aria-label="增加分钟" @click="adjust('minute', 1)">⌄</button></div>
        </div>
      </div>
    </Teleport>
  </div>
</template>

<style>
.nxp-time-popover { position: fixed !important; z-index: 1000; }
</style>
