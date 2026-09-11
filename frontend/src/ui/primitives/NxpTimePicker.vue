<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from "vue";

const props = withDefaults(defineProps<{ id?: string; modelValue?: string; disabled?: boolean; ariaLabel?: string }>(), { id: "", modelValue: "", disabled: false, ariaLabel: "时间" });
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();
const open = ref(false);
const root = ref<HTMLElement | null>(null);
const [initialHour, initialMinute] = String(props.modelValue || "").split(":");
const hour = ref(Number.isFinite(Number(initialHour)) ? Number(initialHour) % 24 : 0);
const minute = ref(Number.isFinite(Number(initialMinute)) ? Number(initialMinute) % 60 : 0);

watch(() => props.modelValue, next => {
  const [nextHour, nextMinute] = String(next || "").split(":");
  if (nextHour !== undefined && Number.isFinite(Number(nextHour))) hour.value = Number(nextHour) % 24;
  if (nextMinute !== undefined && Number.isFinite(Number(nextMinute))) minute.value = Number(nextMinute) % 60;
});

function text(value: number) { return String(value).padStart(2, "0"); }
function value() { return `${text(hour.value)}:${text(minute.value)}`; }
function commit() {
  const next = value();
  emit("update:modelValue", next);
  emit("change", next);
}
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
function toggle() { if (!props.disabled) open.value = !open.value; }
function closeOutside(event: PointerEvent) {
  if (open.value && root.value && event.target instanceof Node && !root.value.contains(event.target)) open.value = false;
}
if (typeof document !== "undefined") document.addEventListener("pointerdown", closeOutside);
onBeforeUnmount(() => document.removeEventListener("pointerdown", closeOutside));
</script>

<template>
  <div ref="root" class="nxp-time">
    <div class="nxp-time-input-wrap"><input :id="props.id || undefined" class="nxp-time-value" type="text" readonly :value="value()" :disabled="disabled" :aria-label="ariaLabel" aria-haspopup="dialog" :aria-expanded="open" @click="toggle"><button class="nxp-time-trigger" type="button" :disabled="disabled" :aria-label="`${ariaLabel}选择`" :aria-expanded="open" @click="toggle">⌄</button></div>
    <div v-show="open" class="nxp-time-popover secondary-surface" role="dialog" :aria-label="ariaLabel">
      <div class="nxp-time-columns">
        <div class="nxp-time-wheel" role="group" aria-label="小时"><button class="nxp-time-step" type="button" aria-label="减少小时" @click="adjust('hour', -1)">⌃</button><div class="nxp-time-viewport" role="listbox" aria-label="小时"><button v-for="offset in [-1, 0, 1]" :key="`h-${offset}`" class="nxp-time-option" :aria-selected="offset === 0" type="button" @click="choose('hour', (hour + offset + 24) % 24)">{{ text((hour + offset + 24) % 24) }}</button></div><button class="nxp-time-step" type="button" aria-label="增加小时" @click="adjust('hour', 1)">⌄</button></div>
        <div class="nxp-time-wheel" role="group" aria-label="分钟"><button class="nxp-time-step" type="button" aria-label="减少分钟" @click="adjust('minute', -1)">⌃</button><div class="nxp-time-viewport" role="listbox" aria-label="分钟"><button v-for="offset in [-1, 0, 1]" :key="`m-${offset}`" class="nxp-time-option" :aria-selected="offset === 0" type="button" @click="choose('minute', (minute + offset + 60) % 60)">{{ text((minute + offset + 60) % 60) }}</button></div><button class="nxp-time-step" type="button" aria-label="增加分钟" @click="adjust('minute', 1)">⌄</button></div>
      </div>
    </div>
  </div>
</template>

<style>
.nxp-time { position: relative; min-width: 0; }
.nxp-time-input-wrap { display: flex; min-width: 0; }
.nxp-time-value { flex: 1 1 auto; min-width: 0; min-height: var(--nx-control-height); padding: 0 12px; border: 1px solid var(--content-control-border, var(--nx-color-border)); border-radius: 8px 0 0 8px; background: var(--content-control, transparent); color: var(--nx-color-text); font: inherit; cursor: pointer; }
.nxp-time-trigger { width: 42px; min-width: 42px; min-height: 40px; padding: 0; border-radius: 0 8px 8px 0; color: var(--muted, var(--nx-color-muted)); font: inherit; }
.nxp-time-trigger:hover, .nxp-time-trigger[aria-expanded="true"] { border-color: var(--accent, var(--nx-color-accent)); background: var(--content-control-hover, transparent); }
.nxp-time-popover { position: absolute; top: calc(100% + 6px); right: 0; z-index: 60; width: 100%; min-width: 0; padding: var(--space-3, var(--nx-space-3)); border: 1px solid var(--content-control-border, var(--nx-color-border)); border-radius: var(--radius-md, var(--nx-radius-md)); background: var(--content-card, var(--nx-color-surface)); box-shadow: var(--shadow, 0 12px 30px rgba(0,0,0,.2)); }
.nxp-time-columns { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
.nxp-time-wheel { display: grid; min-width: 0; grid-template-rows: 30px 96px 30px; }
.nxp-time-step { display: grid; width: 100%; min-height: 30px; place-items: center; padding: 0; border: 0; border-radius: 0; background: transparent; color: var(--muted, var(--nx-color-muted)); font: inherit; box-shadow: none; }
.nxp-time-viewport { display: grid; grid-template-rows: repeat(3, 32px); overflow: hidden; border-top: 1px solid var(--border, var(--nx-color-border)); border-bottom: 1px solid var(--border, var(--nx-color-border)); }
.nxp-time-option { display: grid; width: 100%; min-height: 32px; place-items: center; padding: 0 4px; border: 0; border-radius: 0; background: transparent; color: var(--muted, var(--nx-color-muted)); font: inherit; font-variant-numeric: tabular-nums; box-shadow: none; }
.nxp-time-option[aria-selected="true"] { background: var(--accent-soft, rgba(120,160,230,.15)); color: var(--accent, var(--nx-color-accent)); font-weight: 750; }
</style>
