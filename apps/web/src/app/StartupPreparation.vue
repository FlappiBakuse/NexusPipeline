<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue';
import { t, registerMessages } from './locale';
import { desktop } from './desktop';
import icon from '../../../desktop/nexuspipeline.ico';
import '../../../desktop/startup-content.css';
registerMessages({ '准备界面': 'Preparing interface', '加载插件界面': 'Loading extension interfaces',
  '准备页面数据': 'Preparing page data', '准备首次显示': 'Preparing first display', '已用时 {seconds} 秒': 'Elapsed: {seconds}s' });
defineProps<{ stage: string; completed: number; total: number }>();
const started = performance.now();
const seconds = ref(0);
const timer = setInterval(() => { seconds.value = Math.floor((performance.now() - started) / 1000); }, 250);
onBeforeUnmount(() => clearInterval(timer));
</script>
<template>
  <div class="startup-preparation startup-surface" :class="{ desktop: !!desktop }">
      <img class="startup-icon" :src="icon" width="64" height="64" alt="">
      <h1>NexusPipeline</h1>
      <p class="startup-stage" role="status" aria-live="polite">{{ t(stage) }}<template v-if="total"> · {{ completed }}/{{ total }}</template></p>
      <progress :max="total || 1" :value="total ? completed : undefined" :aria-label="t(stage)" />
      <p class="startup-elapsed">{{ t('已用时 {seconds} 秒').replace('{seconds}', String(seconds)) }}</p>
  </div>
</template>
<style scoped>
.startup-preparation { position: fixed; inset: 0; z-index: 900; }
.startup-preparation.desktop { top: 40px; }
</style>
