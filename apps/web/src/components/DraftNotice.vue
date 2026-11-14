<script setup lang="ts">
import { t } from '../app/locale';
defineProps<{ restored: boolean }>();
</script>
<template>
  <p v-if="restored" class="draft-restored" role="status">{{ t('已恢复未保存的草稿，请及时保存或取消。') }}</p>
</template>
