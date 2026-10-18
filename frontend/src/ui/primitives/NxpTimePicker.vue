<script setup lang="ts">
import NxpIcon from "./NxpIcon.vue";
import NxpSelectionTransition from "../composites/NxpSelectionTransition.vue";
import { nextTick, onBeforeUnmount, ref, watch } from "vue";
import { bindFloatingReposition, positionFloatingOverlay } from "../floating";

const props = withDefaults(defineProps<{ id?: string; modelValue?: string; disabled?: boolean; ariaLabel?: string }>(), { id: "", modelValue: "", disabled: false, ariaLabel: "时间" });
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();
const open = ref(false);
const root = ref<HTMLElement | null>(null);
const anchor = ref<HTMLButtonElement | null>(null);
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
      anchor.value?.focus();
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
    <input :id="props.id || undefined" type="hidden" :value="value()" :disabled="disabled">
    <button ref="anchor" class="nxp-time-trigger" type="button" :disabled="disabled" :aria-label="ariaLabel" aria-haspopup="dialog" :aria-expanded="open" :aria-controls="popoverId" @click="toggle"><span class="nxp-time-value">{{ value() }}</span><NxpIcon name="chevronDown" class-name="nxp-time-chevron" /></button>
    <Teleport to="body">
      <NxpSelectionTransition>
      <div ref="popover" v-show="open" :inert="!open || undefined" :aria-hidden="!open || undefined" :id="popoverId" class="nxp-time-popover secondary-surface" role="dialog" :aria-label="ariaLabel">
        <div class="nxp-time-columns">
          <div class="nxp-time-wheel" role="group" aria-label="小时"><button class="nxp-time-step" type="button" aria-label="减少小时" @click="adjust('hour', -1)">⌃</button><div class="nxp-time-viewport" role="listbox" aria-label="小时"><button v-for="offset in [-1, 0, 1]" :key="`h-${offset}`" class="nxp-time-option" :aria-selected="offset === 0" type="button" @click="choose('hour', (hour + offset + 24) % 24)">{{ text((hour + offset + 24) % 24) }}</button></div><button class="nxp-time-step" type="button" aria-label="增加小时" @click="adjust('hour', 1)"><NxpIcon name="chevronDown" class-name="nxp-time-chevron" /></button></div>
          <div class="nxp-time-wheel" role="group" aria-label="分钟"><button class="nxp-time-step" type="button" aria-label="减少分钟" @click="adjust('minute', -1)">⌃</button><div class="nxp-time-viewport" role="listbox" aria-label="分钟"><button v-for="offset in [-1, 0, 1]" :key="`m-${offset}`" class="nxp-time-option" :aria-selected="offset === 0" type="button" @click="choose('minute', (minute + offset + 60) % 60)">{{ text((minute + offset + 60) % 60) }}</button></div><button class="nxp-time-step" type="button" aria-label="增加分钟" @click="adjust('minute', 1)"><NxpIcon name="chevronDown" class-name="nxp-time-chevron" /></button></div>
        </div>
      </div>
      </NxpSelectionTransition>
    </Teleport>
  </div>
</template>

<style>
.nxp-time { position: relative; min-width: 0; }
.nxp-time-value { min-width: 0; font-variant-numeric: tabular-nums; }
.nxp-time-trigger { display: flex; width: 100%; min-width: 0; min-height: 40px; align-items: center; justify-content: space-between; gap: 10px; padding: 0 12px; border: 1px solid var(--content-control-border, var(--nx-color-border)); border-radius: 8px; background: var(--content-control, transparent); color: var(--nx-color-text); font: inherit; font-size: 13px; text-align: left; }
.nxp-time-trigger:hover, .nxp-time-trigger[aria-expanded="true"] { border-color: var(--accent, var(--nx-color-accent)); background: var(--content-control-hover, transparent); color: var(--nx-color-text); }
.nxp-time-trigger:disabled { cursor: not-allowed; opacity: .45; }
.nxp-time-popover { position: absolute; top: calc(100% + 6px); right: 0; z-index: 60; width: 100%; min-width: 0; padding: var(--space-3, var(--nx-space-3)); border: 1px solid var(--content-control-border, var(--nx-color-border)); border-radius: var(--radius-md, var(--nx-radius-md)); background: var(--content-card, var(--nx-color-surface)); box-shadow: var(--shadow); }
.nxp-time-popover[hidden] { display: none; }
.nxp-time-columns { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
.nxp-time-wheel { display: grid; min-width: 0; grid-template-rows: 40px 120px 40px; }
.nxp-time-step { display: grid; width: 100%; min-height: 40px; place-items: center; padding: 0; border: 0; border-radius: 0; background: transparent; color: var(--muted, var(--nx-color-muted)); font: inherit; font-size: 16px; line-height: 1; box-shadow: none; }
.nxp-time-step:hover, .nxp-time-step:focus-visible { background: var(--content-control-hover, transparent); color: var(--nx-color-text); }
.nxp-time-viewport { display: grid; grid-template-rows: repeat(3, 40px); overflow: hidden; border-top: 1px solid var(--border, var(--nx-color-border)); border-bottom: 1px solid var(--border, var(--nx-color-border)); }
.nxp-time-option { display: grid; width: 100%; min-width: 0; min-height: 40px; place-items: center; padding: 0 4px; border: 0; border-radius: 0; background: transparent; color: var(--muted, var(--nx-color-muted)); font: inherit; font-size: 13px; font-variant-numeric: tabular-nums; box-shadow: none; }
.nxp-time-option:hover, .nxp-time-option:focus-visible { background: var(--content-control-hover, transparent); color: var(--nx-color-text); }
.nxp-time-option[aria-selected="true"] { background: var(--accent-soft, transparent); color: var(--accent, var(--nx-color-accent)); font-weight: 750; box-shadow: inset 0 1px 0 var(--accent, currentColor), inset 0 -1px 0 var(--accent, currentColor); }
.nxp-time-popover { position: fixed !important; z-index: 1000; }
</style>

<style>
.nxp-time-chevron { flex: 0 0 16px; width: 16px; height: 16px; color: var(--muted, var(--nx-color-muted)); transition: transform var(--nx-motion-duration-fast, 160ms) var(--nx-motion-ease, ease); }
.nxp-time-trigger[aria-expanded="true"] .nxp-time-chevron { transform: rotate(180deg); }
</style>
