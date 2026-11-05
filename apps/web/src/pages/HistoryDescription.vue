<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { locale } from '../app/locale';
import { historyDate } from './history-row';
const props = defineProps<{ from: string; until: string; visible: boolean }>();
const target = ref<HTMLElement | null>(null);
onMounted(() => {
  target.value = document.getElementById('page-description-override');
});
const label = computed(() => {
  return [props.from, props.until]
    .map(historyDate)
    .join(locale.value === 'zh-CN' ? ' 至 ' : ' to ');
});
</script>
<template>
  <Teleport :to="target || 'body'" :disabled="!target">
    <span v-if="visible">{{ label }}</span>
  </Teleport>
</template>
