<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { desktop } from './desktop';
import { DesktopCaption } from './desktop-caption';
import { ModalSurface } from '../ui/modal-surface';
import { locale, registerMessages, t } from './locale';

registerMessages({
  最小化: 'Minimize',
  最大化: 'Maximize',
  还原: 'Restore',
  窗口操作: 'Window controls',
  与宿主的连接暂时中断: 'The host connection is temporarily unavailable'
});
const props = defineProps<{ ready: boolean; disconnected: boolean }>();
const emit = defineEmits<{ error: [value: unknown] }>();
const home = ref<HTMLElement>();
const maximized = ref(false);
let generation = 0;
let caption: DesktopCaption | undefined;
let stop: (() => void) | undefined;
let stopLayer: (() => void) | undefined;
function updateWindowState(state: { maximized: boolean }): void {
  if (typeof state?.maximized === 'boolean') maximized.value = state.maximized;
}
function render(): void {
  caption?.render({
    ready: props.ready,
    maximized: maximized.value,
    connection: props.disconnected ? t('与宿主的连接暂时中断') : '',
    labels: {
      controls: t('窗口操作'),
      minimize: t('最小化'),
      resize: t(maximized.value ? '还原' : '最大化'),
      close: t('关闭')
    }
  });
}
watch(() => [props.ready, props.disconnected, maximized.value, locale.value], render);
watch(
  () => props.ready,
  async (ready) => {
    const current = ++generation;
    if (!ready || !desktop) return;
    try {
      const state = await desktop.windowState();
      if (current === generation) updateWindowState(state);
    } catch (error) {
      if (current === generation) emit('error', error);
    }
  },
  { immediate: true }
);
async function act(action: 'minimize' | 'toggleMaximize' | 'close'): Promise<void> {
  if (!props.ready || !desktop) return;
  try {
    await desktop[action]();
  } catch (error) {
    emit('error', error);
  }
}
onMounted(() => {
  document.documentElement.dataset.desktop = 'true';
  caption = new DesktopCaption(home.value!, (value) => {
    void act(value);
  });
  render();
  stop = desktop?.onWindowState(updateWindowState);
  stopLayer = ModalSurface.watchLayer((dialog) => caption?.move(dialog));
});
onBeforeUnmount(() => {
  generation++;
  stop?.();
  stopLayer?.();
  caption?.dispose();
  delete document.documentElement.dataset.desktop;
});
</script>
<template><div ref="home" class="desktop-titlebar-space" /></template>
<style scoped>
.desktop-titlebar-space {
  height: 40px;
  min-height: 40px;
}
</style>
