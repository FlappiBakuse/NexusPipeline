<script setup lang="ts">
import { computed, ref, watch, onBeforeUnmount, nextTick } from 'vue';
import type { LiveLogs, LogIdentity, LogWindow } from '../control/live-logs';
import { LiveLogTail } from '../control/live-log-tail';
import { t, registerMessages } from '../app/locale';
import ScriptLogPanel from './ScriptLogPanel.vue';
import { needsLogSeparator, scriptLogRoles, type ScriptLogSelection } from './script-log-roles';

const props = defineProps<{
  subscribe: LiveLogs['subscribe']; identity?: Omit<LogIdentity, 'role'>; roles: readonly string[]; visible: boolean;
}>();
const value = ref<LogWindow>({ text: '', sequence: 0, omitted: false, connected: false });
const height = ref(Math.max(180, Math.min(360, Math.round(innerHeight * 0.3))));
const maximum = ref(Math.max(180, Math.min(720, Math.floor(innerHeight * 0.75))));
const root = ref<HTMLElement>();
const panel = ref<InstanceType<typeof ScriptLogPanel>>();
const roleSelection = ref<ScriptLogSelection>('all');
const inView = ref(false);
let follow = true;
let release: (() => void) | undefined;
let observer: IntersectionObserver | undefined;
let drag: { pointer: number; y: number; height: number } | undefined;
let selected = '';
let generation = 0;
let renderTimer: ReturnType<typeof setTimeout> | undefined;
const roles = computed(() => scriptLogRoles.filter(role => props.roles.includes(role)));
const selectedRole = computed(() => roleSelection.value !== 'all' && roles.value.includes(roleSelection.value)
  ? roleSelection.value : 'all');
const windows = new Map<string, LogWindow>();
function renderWindow(epoch: number): void {
  renderTimer = undefined;
  if (epoch !== generation || !props.visible || !inView.value) return;
  const ordered = (selectedRole.value === 'all' ? roles.value : [selectedRole.value]).map(key => windows.get(key));
  const tail = new LiveLogTail();
  for (const item of ordered) {
    if (!item?.text) continue;
    if (needsLogSeparator(tail.text, item.text)) tail.append('\n');
    tail.append(item.text);
  }
  value.value = {
    text: tail.text,
    sequence: Math.max(0, ...ordered.map((item) => item?.sequence || 0)),
    omitted: tail.omitted || ordered.some((item) => item?.omitted),
    connected: ordered.some((item) => item?.connected),
    error: ordered.find((item) => item?.error)?.error
  };
  if (follow) void nextTick(() => {
    if (epoch !== generation || !props.visible || !inView.value) return;
    const element = panel.value?.viewport;
    if (element) element.scrollTop = element.scrollHeight;
  });
}
registerMessages({
  暂无日志输出: 'No log output',
  调整日志高度: 'Adjust log height',
  较早日志已折叠: 'Earlier log output is folded',
  日志读取失败: 'Unable to read the log'
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
    const epoch = ++generation;
    clearTimeout(renderTimer); renderTimer = undefined;
    release?.();
    release = undefined;
    const identity = JSON.stringify(props.identity);
    if (identity !== selected) {
      selected = identity;
      windows.clear();
      roleSelection.value = 'all';
      value.value = { text: '', sequence: 0, omitted: false, connected: false };
      follow = true;
    }
    if (!props.visible || !inView.value || !props.identity) return;
    if (selectedRole.value === 'all') roleSelection.value = 'all';
    renderTimer = setTimeout(() => renderWindow(epoch), 200);
    const subscriptions = roles.value.map((role) =>
      props.subscribe({ ...props.identity!, role }, (next) => {
        if (epoch !== generation) return;
        const previous = windows.get(role);
        if (!next.connected && !next.text && !next.error && previous) {
          if (!previous.error) return;
          windows.set(role, { ...previous, error: undefined });
        } else windows.set(role, next);
        renderTimer ??= setTimeout(() => renderWindow(epoch), 200);
      })
    );
    release = () => subscriptions.forEach((unsubscribe) => unsubscribe());
  },
  { immediate: true }
);

function selectRole(role: ScriptLogSelection): void {
  if (role === selectedRole.value) return;
  roleSelection.value = role;
  follow = true;
  clearTimeout(renderTimer);
  renderWindow(generation);
}
function scroll(): void {
  const element = panel.value?.viewport;
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
  generation++; clearTimeout(renderTimer);
  release?.();
  observer?.disconnect();
  window.removeEventListener('resize', viewportChanged);
  drag = undefined;
});
</script>

<template>
  <div ref="root" class="run-log-resizable" :style="{ height: height + 'px' }">
    <ScriptLogPanel ref="panel" :title="t('实时日志')" :roles="roles" :selected="selectedRole" live
      @select="selectRole" @scroll="scroll">
      <div v-if="value.error" role="status">{{ t('日志读取失败') }} · {{ value.error }}</div>
      <pre><span v-if="value.omitted" class="omitted">{{ t('较早日志已折叠') }}</span>{{ value.text || '(' + t('暂无日志输出') + ')' }}</pre>
    </ScriptLogPanel>
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
  border-radius: var(--nx-radius-pill);
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
  border-radius: var(--nx-radius-pill);
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
