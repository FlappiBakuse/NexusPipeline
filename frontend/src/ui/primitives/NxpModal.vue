<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, useAttrs, watch } from "vue";
import NxpIcon from "./NxpIcon.vue";
import NxpScrollArea from "./NxpScrollArea.vue";

const attrs = useAttrs();
const props = withDefaults(defineProps<{
  open?: boolean;
  title?: string;
  ariaLabel?: string;
  closeLabel?: string;
  closeable?: boolean;
  locked?: boolean;
  size?: "default" | "wide";
  panelClass?: string;
  bodyClass?: string;
}>(), {
  open: false,
  title: "",
  ariaLabel: "",
  closeLabel: "Close",
  closeable: true,
  locked: false,
  size: "default",
  panelClass: "",
  bodyClass: "",
});
const emit = defineEmits<{ close: [] }>();
const root = ref<HTMLElement | null>(null);
const panel = ref<HTMLElement | null>(null);
let returnFocus: HTMLElement | null = null;

function focusableElements() {
  return Array.from(root.value?.querySelectorAll<HTMLElement>("button, input, select, textarea, a[href], [tabindex]:not([tabindex='-1'])") || [])
    .filter(element => !element.matches(".nxp-modal-backdrop"))
    .filter(element => !element.hasAttribute("disabled") && !element.closest("[hidden]"));
}

function focusInitial() {
  const first = focusableElements()[0];
  (first || panel.value || root.value)?.focus({ preventScroll: true });
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
    (panel.value || root.value).focus({ preventScroll: true });
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

function preventBackdropFocus(event: PointerEvent) {
  event.preventDefault();
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
    <div v-if="props.open" ref="root" v-bind="attrs" class="modal-mask nxp-modal" role="dialog" aria-modal="true" tabindex="-1" :aria-label="props.ariaLabel || props.title || undefined">
      <div v-if="props.closeable" class="nxp-modal-backdrop" aria-hidden="true" @pointerdown="preventBackdropFocus" @click="onBackdropClick"></div>
      <section ref="panel" class="modal nxp-modal-panel" :class="[props.panelClass, { wide: props.size === 'wide' }]" tabindex="-1" :data-locked="props.locked ? '' : undefined">
        <header v-if="title || closeable || $slots.header" class="nxp-modal-header modal-header">
          <slot name="header"><h2 v-if="title" class="modal-title">{{ title }}</h2></slot>
          <button v-if="closeable" class="icon-button modal-close" type="button" :aria-label="props.closeLabel" @click="emit('close')"><NxpIcon name="close" /></button>
        </header>
        <NxpScrollArea class="nxp-modal-body modal-body" :class="props.bodyClass"><slot /></NxpScrollArea>
        <footer v-if="$slots.footer" class="nxp-modal-footer modal-footer"><slot name="footer" /></footer>
      </section>
    </div>
  </Transition>
</template>

<style>
.nxp-modal { position: fixed; inset: 0; z-index: 100; display: flex; align-items: center; justify-content: center; padding: 14px; background: var(--mask, rgba(0, 0, 0, .5)); }
.nxp-modal-backdrop { position: absolute; inset: 0; background: transparent; cursor: default; }
.nxp-modal-panel { position: relative; z-index: 1; display: flex; width: min(680px, 100%); max-height: min(88vh, 900px); min-height: 0; flex-direction: column; overflow: hidden; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-lg); background: var(--nx-color-surface); color: var(--nx-color-text); box-shadow: var(--shadow, 0 16px 40px rgba(0, 0, 0, .28)); }
.nxp-modal-panel.wide { width: min(960px, 100%); }
.nxp-modal-header { display: flex; flex: 0 0 auto; align-items: flex-start; justify-content: space-between; gap: var(--nx-space-3); margin: 0; padding: clamp(18px, 3vw, 30px) clamp(18px, 3vw, 30px) var(--nx-space-4); border-bottom: 0; }
.nxp-modal-header h2 { margin: 0; font-size: 19px; }
.nxp-modal-header button { flex: 0 0 auto; }
.nxp-modal-header button .nxp-icon { width: 18px; height: 18px; }
.nxp-modal-body { min-height: 0; flex: 1 1 auto; padding: 0 clamp(18px, 3vw, 30px) var(--nx-space-4); }
.nxp-modal-footer { display: flex; flex: 0 0 auto; align-items: center; justify-content: flex-end; flex-wrap: wrap; gap: var(--nx-space-3); margin: 0; padding: var(--nx-space-4) clamp(18px, 3vw, 30px) clamp(18px, 3vw, 30px); border-top: 1px solid var(--nx-color-border); border-bottom: 0; }
.nxp-modal:focus, .nxp-modal-panel:focus { outline: none; }
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
