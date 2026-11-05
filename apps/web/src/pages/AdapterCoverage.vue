<script setup lang="ts">
import type { AdapterResult } from './adapter-results';
import { t, registerMessages } from '../app/locale';
registerMessages({ '完整覆盖': 'Full coverage', '部分覆盖': 'Partial coverage', '已启用业务任务': 'Selected business tasks',
  '可安全重试': 'Safe to retry', '游戏路径一致': 'Game path matches', '游戏路径不一致': 'Game path differs',
  '游戏路径无法确认': 'Game path is unverified' });
defineProps<{ coverage?: AdapterResult['coverage']; gamePathCheck?: string }>();
</script>
<template>
  <section v-if="coverage" class="adapter-coverage">
    <strong>{{ t(coverage.isComplete ? '完整覆盖' : '部分覆盖') }}</strong>
    <p>{{ t('已启用业务任务') }} {{ coverage.selected }} · {{ t('可安全重试') }} {{ coverage.retrySafe }}</p>
    <p v-if="gamePathCheck && gamePathCheck !== 'notApplicable'" :role="gamePathCheck === 'mismatch' ? 'alert' : 'status'">
      {{ t(gamePathCheck === 'matches' ? '游戏路径一致' : gamePathCheck === 'mismatch' ? '游戏路径不一致' : '游戏路径无法确认') }}
    </p>
  </section>
</template>
