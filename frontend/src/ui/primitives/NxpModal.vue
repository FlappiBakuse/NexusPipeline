<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, useAttrs, watch } from "vue";
import NxpIcon from "./NxpIcon.vue";

const attrs = useAttrs();
const props = withDefaults(defineProps<{ open?: boolean; title?: string; ariaLabel?: string; closeable?: boolean; locked?: boolean; size?: "default" | "wide"; panelClass?: string; bodyClass?: string }>(), { open: false, title: "", ariaLabel: "", closeable: true, locked: false, size: "default", panelClass: "", bodyClass: "" });
const emit = defineEmits<{ close: [] }>();
const root = ref<HTMLElement | null>(null);
let returnFocus: HTMLElement | null = null;

function focusableElements() {
  return Array.from(root.value?.querySelectorAll<HTMLElement>("button, input, select, textarea, a[href], [tabindex]:not([tabindex='-1'])") || [])
    .filter(element => !element.matches(".nxp-modal-backdrop"))
    .filter(element => !element.hasAttribute("disabled") && !element.closest("[hidden]"));
}

function focusInitial() {
  const first = focusableElements()[0];
  (first || root.value)?.focus({ preventScroll: true });
}

function restoreFocus() {
  if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
  returnFocus = null;
}

function onKeydown(event: KeyboardEvent) {
  if (!props.open || !root.value) return;
  if (event.key === "Escape") {
    if (!props.locked && props.closeable) {
      event.preventDefault();
      emit("close");
    }
    return;
  }
  if (event.key !== "Tab") return;
  const elements = focusableElements();
  if (!elements.length) {
    event.preventDefault();
    root.value.focus({ preventScroll: true });
    return;
  }
  const first = elements[0];
  const last = elements[elements.length - 1];
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last.focus({ preventScroll: true });
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first.focus({ preventScroll: true });
  }
}

function onBackdropClick() {
  if (!props.locked && props.closeable) emit("close");
}

watch(() => props.open, value => {
  if (value) {
    returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    void nextTick(focusInitial);
  } else {
    restoreFocus();
  }
});

onMounted(() => {
  document.addEventListener("keydown", onKeydown, true);
  if (props.open) {
    returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    void nextTick(focusInitial);
  }
});
onBeforeUnmount(() => {
  document.removeEventListener("keydown", onKeydown, true);
  restoreFocus();
});
</script>

<template>
  <Transition name="nxp-modal" appear>
    <div v-if="props.open" ref="root" v-bind="attrs" class="nxp-modal" role="dialog" aria-modal="true" tabindex="-1" :aria-label="props.ariaLabel || props.title || undefined">
      <button v-if="props.closeable" class="nxp-modal-backdrop" type="button" aria-label="关闭" @click="onBackdropClick"></button>
      <section class="nxp-modal-panel" :class="[props.panelClass, { wide: props.size === 'wide' }]" tabindex="-1">
        <header v-if="title || closeable" class="nxp-modal-header modal-header"><h2 v-if="title">{{ title }}</h2><button v-if="closeable" class="modal-close" type="button" aria-label="关闭" @click="emit('close')"><NxpIcon name="close" /></button></header>
        <div class="nxp-modal-body modal-body" :class="props.bodyClass"><slot /></div>
        <footer v-if="$slots.footer" class="nxp-modal-footer modal-footer"><slot name="footer" /></footer>
      </section>
    </div>
  </Transition>
</template>

<style>
.nxp-modal { position: fixed; inset: 0; z-index: 100; display: grid; place-items: center; padding: var(--nx-space-5); }
.nxp-modal-backdrop { position: absolute; inset: 0; border: 0; background: var(--mask, rgba(0, 0, 0, .5)); cursor: default; }
.nxp-modal-panel { position: relative; z-index: 1; width: min(560px, 100%); max-height: min(90vh, 720px); overflow: auto; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-lg); background: var(--nx-color-surface); color: var(--nx-color-text); box-shadow: var(--shadow, 0 16px 40px rgba(0, 0, 0, .28)); }
.nxp-modal-panel.wide { width: min(960px, 100%); }
.nxp-modal-header, .nxp-modal-footer { display: flex; align-items: center; justify-content: space-between; gap: var(--nx-space-3); padding: var(--nx-space-4) var(--nx-space-5); border-bottom: 1px solid var(--nx-color-border); }
.nxp-modal-header h2 { margin: 0; font-size: 16px; }
.nxp-modal-header button { display: inline-flex; min-width: 36px; min-height: 36px; align-items: center; justify-content: center; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-md); background: transparent; color: var(--nx-color-muted, var(--nx-color-text)); cursor: pointer; }
.nxp-modal-header button:hover { background: var(--nx-color-surface-hover, transparent); color: var(--nx-color-text); }
.nxp-modal-header button .nxp-icon { width: 18px; height: 18px; }
.nxp-modal-body { padding: var(--nx-space-5); }
.nxp-modal-body.legacy-modal-body { padding: 0; }
.nxp-modal-footer { justify-content: flex-end; border-top: 1px solid var(--nx-color-border); border-bottom: 0; }
.nxp-modal-enter-active,
.nxp-modal-leave-active { transition: opacity var(--nx-motion-duration-fast) var(--nx-motion-ease); }
.nxp-modal-enter-active .nxp-modal-backdrop,
.nxp-modal-leave-active .nxp-modal-backdrop { transition: opacity var(--nx-motion-duration-fast) var(--nx-motion-ease); }
.nxp-modal-enter-active .nxp-modal-panel,
.nxp-modal-leave-active .nxp-modal-panel { transition: opacity var(--nx-motion-duration-normal) var(--nx-motion-ease), transform var(--nx-motion-duration-normal) var(--nx-motion-ease); }
.nxp-modal-enter-from,
.nxp-modal-leave-to { opacity: 0; }
.nxp-modal-enter-from .nxp-modal-backdrop,
.nxp-modal-leave-to .nxp-modal-backdrop { opacity: 0; }
.nxp-modal-enter-from .nxp-modal-panel,
.nxp-modal-leave-to .nxp-modal-panel { opacity: 0; transform: translateY(5px) scale(.985); }
</style>
