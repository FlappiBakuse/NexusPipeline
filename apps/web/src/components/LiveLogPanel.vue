<script setup lang="ts">
import { computed, ref, watch, onBeforeUnmount, nextTick } from 'vue';
import type { AppStore } from '../app/store';
import type { LogIdentity, LogWindow } from '../control/live-logs';
import { t, registerMessages } from '../app/locale';

const props = defineProps<{
  store: AppStore; identity?: Omit<LogIdentity, 'role'>; roles: readonly string[]; visible: boolean;
}>();
const value = ref<LogWindow>({ text: '', sequence: 0, omitted: false, connected: false });
const height = ref(Math.max(180, Math.min(360, Math.round(innerHeight * 0.3))));
const maximum = ref(Math.max(180, Math.min(720, Math.floor(innerHeight * 0.75))));
const root = ref<HTMLElement>();
const windowElement = ref<HTMLElement>();
const inView = ref(false);
let follow = true;
let release: (() => void) | undefined;
let observer: IntersectionObserver | undefined;
let drag: { pointer: number; y: number; height: number } | undefined;
let selected = '';
const roles = computed(() => ['preHook', 'main', 'postHook'].filter(role => props.roles.includes(role)));
const windows = new Map<string, LogWindow>();
registerMessages({
  运行日志: 'Run log',
  暂无日志输出: 'No log output',
  调整日志高度: 'Adjust log height',
  较早日志已折叠: 'Earlier log output is folded'
});

watch(root, (element) => {
  observer?.disconnect();
  if (!element) return;
  observer = new IntersectionObserver((entries) => {
    inView.value = entries.some((entry) => entry.isIntersecting);
  });
  observer.observe(element);
});
watch(
  [() => JSON.stringify(props.identity), () => roles.value.join(), () => props.visible, inView],
  () => {
    release?.();
    release = undefined;
    const identity = JSON.stringify(props.identity);
    if (identity !== selected) {
      selected = identity;
      windows.clear();
      value.value = { text: '', sequence: 0, omitted: false, connected: false };
      follow = true;
    }
    if (!props.visible || !inView.value || !props.identity) return;
    const subscriptions = roles.value.map((role) =>
      props.store.logs.subscribe({ ...props.identity!, role }, (next) => {
        if (!next.connected && !next.text && windows.has(role)) return;
        windows.set(role, next);
        const ordered = roles.value.map((key) => windows.get(key));
        const text = ordered.map((item) => item?.text || '').join('');
        const lines = text.split('\n');
        value.value = {
          text: lines
            .slice(-500)
            .join('\n')
            .slice(-256 * 1024),
          sequence: Math.max(0, ...ordered.map((item) => item?.sequence || 0)),
          omitted: lines.length > 500 || text.length > 256 * 1024 || ordered.some((item) => item?.omitted),
          connected: ordered.some((item) => item?.connected)
        };
        if (follow)
          void nextTick(() => {
            const element = windowElement.value;
            if (element) element.scrollTop = element.scrollHeight;
          });
      })
    );
    release = () => subscriptions.forEach((unsubscribe) => unsubscribe());
  },
  { immediate: true }
);

function scroll(): void {
  const element = windowElement.value;
  if (element) follow = element.scrollHeight - element.clientHeight - element.scrollTop <= 24;
}
function resize(next: number): void {
  height.value = Math.max(180, Math.min(maximum.value, Math.round(next)));
}
function begin(event: PointerEvent): void {
  if (event.button !== 0) return;
  drag = { pointer: event.pointerId, y: event.clientY, height: height.value };
  (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
  event.preventDefault();
}
function move(event: PointerEvent): void {
  if (drag?.pointer === event.pointerId) resize(drag.height + event.clientY - drag.y);
}
function key(event: KeyboardEvent): void {
  const delta = ['ArrowDown', 'PageDown'].includes(event.key)
    ? 24
    : ['ArrowUp', 'PageUp'].includes(event.key)
      ? -24
      : 0;
  if (!delta && !['Home', 'End'].includes(event.key)) return;
  event.preventDefault();
  resize(event.key === 'Home' ? 180 : event.key === 'End' ? maximum.value : height.value + delta);
}
function viewportChanged(): void {
  maximum.value = Math.max(180, Math.min(720, Math.floor(innerHeight * 0.75)));
  resize(height.value);
}
window.addEventListener('resize', viewportChanged);
onBeforeUnmount(() => {
  release?.();
  observer?.disconnect();
  window.removeEventListener('resize', viewportChanged);
  drag = undefined;
});
</script>

<template>
  <div ref="root" class="run-log-resizable" :style="{ height: height + 'px' }">
    <nexus-scroll-area ref="windowElement" class="run-log" :label="t('运行日志')" @scroll="scroll">
      <pre><span v-if="value.omitted" class="omitted">{{ t('较早日志已折叠') }}</span>{{ value.text || '(' + t('暂无日志输出') + ')' }}</pre>
    </nexus-scroll-area>
    <div
      class="log-resize"
      role="separator"
      tabindex="0"
      aria-orientation="horizontal"
      :aria-label="t('调整日志高度')"
      :aria-valuemin="180"
      :aria-valuemax="maximum"
      :aria-valuenow="height"
      @pointerdown="begin"
      @pointermove="move"
      @lostpointercapture="drag = undefined"
      @keydown="key"
    />
  </div>
</template>

<style scoped>
.run-log-resizable {
  position: relative;
  min-width: 0;
  min-height: 180px;
  margin-top: 12px;
}
.run-log {
  width: 100%;
  height: 100%;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--log-bg);
  color: var(--log-text);
}
pre {
  margin: 0;
  min-height: 100%;
  padding: 13px;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font:
    12px/1.6 'Cascadia Mono',
    Consolas,
    'Courier New',
    monospace;
}
.omitted {
  display: block;
  color: var(--muted);
}
.log-resize {
  position: absolute;
  z-index: 3;
  bottom: 0;
  left: 10px;
  right: 10px;
  height: 10px;
  border-top: 2px solid color-mix(in srgb, var(--accent) 55%, transparent);
  border-radius: 2px;
  cursor: ns-resize;
  opacity: 0.35;
  touch-action: none;
}
.log-resize::after {
  content: '';
  position: absolute;
  top: 3px;
  left: 35%;
  right: 35%;
  height: 2px;
  border-radius: 2px;
  background: currentColor;
}
.log-resize:hover,
.log-resize:focus-visible {
  opacity: 1;
}
.log-resize:focus-visible {
  outline: none;
  box-shadow: var(--nx-focus-ring);
}
</style>
