<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { t } from "../../platform/i18n";
import NxpIcon from "../primitives/NxpIcon.vue";

/** 二级页面语义的自定义浮层：`role="dialog"` + 可选的右上角关闭入口。
 *  普通控件 popover（Select、TimePicker 等）不使用本组件。定位由调用方 class 提供。 */
const props = withDefaults(defineProps<{
  open?: boolean;
  id?: string;
  ariaLabel?: string;
  closeLabel?: string;
  closeable?: boolean;
  labelledby?: string;
}>(), {
  open: false,
  id: undefined,
  ariaLabel: "",
  closeLabel: "",
  closeable: true,
  labelledby: "",
});

const emit = defineEmits<{ close: [] }>();

const root = ref<HTMLElement | null>(null);
let returnFocus: HTMLElement | null = null;

function focusInitial() {
  const first = root.value?.querySelector<HTMLElement>("button, input, select, textarea, a[href], [tabindex]:not([tabindex='-1'])");
  (first || root.value)?.focus?.({ preventScroll: true });
}

function restoreFocus() {
  if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
  returnFocus = null;
}

function close() {
  emit("close");
}

function onKeydown(event: KeyboardEvent) {
  if (event.key !== "Escape" || !props.open || !root.value) return;
  event.preventDefault();
  event.stopPropagation();
  close();
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
  <div
    v-if="props.open"
    :id="props.id"
    ref="root"
    class="nxp-dialog-popover"
    role="dialog"
    tabindex="-1"
    :aria-label="props.ariaLabel || undefined"
    :aria-labelledby="props.labelledby || undefined"
    @click.stop
  >
    <button
      v-if="props.closeable"
      class="icon-button nxp-dialog-popover-close"
      type="button"
      :aria-label="props.closeLabel || t('common.close', {}, 'Close')"
      @click.stop="close"
    >
      <NxpIcon name="close" />
    </button>
    <slot />
  </div>
</template>

<style>
.nxp-dialog-popover { position: absolute; }
.nxp-dialog-popover:focus { outline: none; }
.nxp-dialog-popover-close { position: absolute; top: 6px; right: 6px; z-index: 1; display: inline-flex; width: 32px; height: 32px; align-items: center; justify-content: center; border: 1px solid transparent; border-radius: var(--radius-sm); background: transparent; color: var(--muted); }
.nxp-dialog-popover-close:hover, .nxp-dialog-popover-close:focus-visible { border-color: var(--content-control-border); background: var(--content-control-hover); color: var(--text); }
.nxp-dialog-popover-close .nxp-icon { width: 16px; height: 16px; }
</style>
