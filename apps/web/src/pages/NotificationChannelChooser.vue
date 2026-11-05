<script setup lang="ts">
import ModalTransition from '../components/ModalTransition.vue';
import { t } from '../app/locale';
import type { NotificationChannel } from './notification-types';
defineProps<{ visible: boolean }>();
const emit = defineEmits<{ select: [channel: NotificationChannel]; close: [] }>();
</script>
<template>
  <ModalTransition>
    <nexus-modal :open.prop="visible" :title="t('添加通知任务')" @nexus-close="emit('close')">
      <div class="channel-choices">
        <button aria-label="SMTP" @click="emit('select', 'smtp')">
          <strong>SMTP</strong>
          <span>{{ t('通过邮件服务器发送通知。') }}</span>
        </button>
        <button aria-label="Webhook" @click="emit('select', 'feishu')">
          <strong>Webhook</strong>
          <span>{{ t('发送到飞书、企业微信、Slack、Discord 或自定义 Webhook。') }}</span>
        </button>
      </div>
    </nexus-modal>
  </ModalTransition>
</template>
<style scoped src="./notification-channel-chooser.css"></style>
