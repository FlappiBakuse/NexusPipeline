<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import { api, isAbortError } from "../../platform/api";
import { t } from "../../platform/i18n";
import { toast } from "../../platform/toast";
import { registerRecoveryDirtyGuard } from "../../platform/service-recovery";
import NxpModal from "../../ui/primitives/NxpModal.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpField from "../../ui/primitives/NxpField.vue";
import NxpTextInput from "../../ui/primitives/NxpTextInput.vue";
import NxpTextArea from "../../ui/primitives/NxpTextArea.vue";
import NxpSelect from "../../ui/primitives/NxpSelect.vue";
import NxpNumberInput from "../../ui/primitives/NxpNumberInput.vue";
import NxpSwitchSetting from "../../ui/composites/NxpSwitchSetting.vue";
import NotificationSecretField from "./NotificationSecretField.vue";
import { newNotification, type NotificationTask } from "./notificationTypes";
const props = defineProps<{ open: boolean; task: NotificationTask | null; type: "smtp" | "webhook" }>();
const emit = defineEmits<{ close: []; saved: [] }>();
const draft = ref(newNotification("smtp"));
const baseline = ref("");
const busy = ref(false);
const error = ref("");
const controller = new AbortController();
const dirty = computed(() => JSON.stringify(draft.value) !== baseline.value);
const unregister = registerRecoveryDirtyGuard(() => props.open && dirty.value);
onBeforeUnmount(() => { controller.abort(); unregister(); });
watch(() => props.open, open => {
  if (!open) return;
  draft.value = props.task ? { name: props.task.name, remark: props.task.remark, type: props.task.type, enabled: props.task.enabled, configuration: { ...props.task.configuration }, secrets: {} } : newNotification(props.type);
  for (const key of ["smtpPassword", "webhookUrl", "webhookSecret", "feishuAppSecret", "slackBotToken"]) draft.value.secrets[key] = { action: "keep", value: "" };
  baseline.value = JSON.stringify(draft.value); error.value = "";
}, { immediate: true });
async function save() {
  if (busy.value) return;
  busy.value = true; error.value = "";
  try {
    await api(props.task ? "PUT" : "POST", `/api/notifications${props.task ? `/${props.task.id}` : ""}`, draft.value, controller.signal);
    baseline.value = JSON.stringify(draft.value); emit("saved");
  } catch (reason) { if (!isAbortError(reason)) error.value = reason instanceof Error ? reason.message : String(reason); }
  finally { busy.value = false; }
}
async function test() {
  if (dirty.value) { toast(t("notifications.save_first"), "info"); return; }
  if (!props.task || busy.value) return;
  busy.value = true;
  try { await api("POST", `/api/notifications/${props.task.id}/test`, undefined, controller.signal); toast(t("settings.notification.test_success"), "info"); }
  catch (reason) { if (!isAbortError(reason)) error.value = reason instanceof Error ? reason.message : String(reason); }
  finally { busy.value = false; }
}
</script>
<template>
  <NxpModal :open="open" :title="t(task ? 'notifications.edit' : 'notifications.add')" :locked="busy" size="wide" @close="emit('close')">
    <section class="notification-editor">
      <NxpField :label="t('notifications.name')" required><NxpTextInput v-model="draft.name" :aria-label="t('notifications.name')" /></NxpField>
      <NxpField :label="t('notifications.remark')"><NxpTextArea v-model="draft.remark" :aria-label="t('notifications.remark')" /></NxpField>
      <div class="settings-list">
        <NxpSwitchSetting v-model="draft.enabled" :label="t('notifications.enable')" :description="t('notifications.enable_help')" />
        <NxpSwitchSetting :model-value="Boolean(draft.configuration.screenshotEnabled)" :label="t('settings.send_screenshots')" :description="t('settings.notification.screenshot_scope')" @update:model-value="draft.configuration.screenshotEnabled = $event" />
      </div>
      <template v-if="draft.type === 'smtp'">
        <div class="form-grid three">
          <NxpField :label="t('settings.smtp_server')"><NxpTextInput :model-value="String(draft.configuration.smtpHost || '')" @update:model-value="draft.configuration.smtpHost = $event" :aria-label="t('settings.smtp_server')" /></NxpField>
          <NxpField :label="t('settings.port')"><NxpNumberInput :model-value="Number(draft.configuration.smtpPort)" :min="1" :max="65535" :aria-label="t('settings.port')" @update:model-value="draft.configuration.smtpPort = Number($event)" /></NxpField>
          <NxpField :label="t('settings.encryption')"><NxpSelect :model-value="String(draft.configuration.smtpSecure)" :options="['auto','ssl','starttls','none'].map(value => ({ value, label: value.toUpperCase() }))" :aria-label="t('settings.encryption')" @update:model-value="draft.configuration.smtpSecure = String($event)" /></NxpField>
        </div>
        <div class="form-grid">
          <NxpField :label="t('common.account')"><NxpTextInput :model-value="String(draft.configuration.smtpUser || '')" @update:model-value="draft.configuration.smtpUser = $event" :aria-label="t('common.account')" /></NxpField>
          <NotificationSecretField v-model="draft.secrets.smtpPassword" :label="t('settings.smtp_password')" :configured="task?.secrets.smtpPassword" />
        </div>
        <div class="form-grid">
          <NxpField :label="t('settings.notification.recipients_help')"><NxpTextInput :model-value="String(draft.configuration.smtpTo || '')" @update:model-value="draft.configuration.smtpTo = $event" :aria-label="t('settings.notification.recipients_help')" /></NxpField>
          <NxpField :label="t('settings.from_address_blank_account')"><NxpTextInput :model-value="String(draft.configuration.smtpFrom || '')" @update:model-value="draft.configuration.smtpFrom = $event" :aria-label="t('settings.from_address_blank_account')" /></NxpField>
        </div>
        <div class="form-grid">
          <NxpField :label="t('settings.subject_prefix')"><NxpTextInput :model-value="String(draft.configuration.smtpSubjectPrefix || '')" @update:model-value="draft.configuration.smtpSubjectPrefix = $event" :aria-label="t('settings.subject_prefix')" /></NxpField>
          <NxpField :label="t('settings.timeout_seconds')"><NxpNumberInput :model-value="Number(draft.configuration.timeout)" :min="1" :max="300" :aria-label="t('settings.timeout_seconds')" @update:model-value="draft.configuration.timeout = Number($event)" /></NxpField>
        </div>
      </template>
      <template v-else>
        <div class="form-grid">
          <NxpField :label="t('settings.webhook_type')"><NxpSelect :model-value="String(draft.configuration.webhookType)" :options="['feishu','wecom','slack','discord','generic'].map(value => ({ value, label: value === 'feishu' ? 'Feishu' : value === 'wecom' ? 'WeCom' : value === 'slack' ? 'Slack' : value === 'discord' ? 'Discord' : 'Generic' }))" :aria-label="t('settings.webhook_type')" @update:model-value="draft.configuration.webhookType = String($event)" /></NxpField>
          <NxpField :label="t('settings.timeout_seconds')"><NxpNumberInput :model-value="Number(draft.configuration.timeout)" :min="1" :max="300" :aria-label="t('settings.timeout_seconds')" @update:model-value="draft.configuration.timeout = Number($event)" /></NxpField>
        </div>
        <div class="form-grid">
          <NotificationSecretField v-model="draft.secrets.webhookUrl" type="text" :label="t('settings.webhook_address')" :help="t('settings.notification.webhook_url_keep')" :configured="task?.secrets.webhookUrl" />
          <NotificationSecretField v-model="draft.secrets.webhookSecret" :label="t('settings.webhook_signing_secret')" :help="t('settings.notification.secret_keep')" :configured="task?.secrets.webhookSecret" />
        </div>
        <div v-if="draft.configuration.webhookType === 'feishu'" class="form-grid">
          <NxpField :label="t('settings.feishu_app_id')" :help="t('settings.notification.image_credentials')"><NxpTextInput :model-value="String(draft.configuration.feishuAppId || '')" @update:model-value="draft.configuration.feishuAppId = $event" :aria-label="t('settings.feishu_app_id')" /></NxpField>
          <NotificationSecretField v-model="draft.secrets.feishuAppSecret" :label="t('settings.feishu_app_secret')" :help="t('settings.notification.app_secret_keep')" :configured="task?.secrets.feishuAppSecret" />
        </div>
        <div v-else-if="draft.configuration.webhookType === 'slack'" class="form-grid">
          <NxpField :label="t('settings.slack_channel_id')" :help="t('settings.notification.bot_member_help')"><NxpTextInput :model-value="String(draft.configuration.slackChannelId || '')" @update:model-value="draft.configuration.slackChannelId = $event" :aria-label="t('settings.slack_channel_id')" /></NxpField>
          <NotificationSecretField v-model="draft.secrets.slackBotToken" :label="t('settings.slack_bot_token')" :help="t('settings.notification.bot_token_keep')" :configured="task?.secrets.slackBotToken" />
        </div>
        <NxpField v-if="draft.configuration.webhookType === 'generic'" :label="t('settings.notification.custom_template')" :help="t('settings.notification.template_help')"><NxpTextArea :model-value="String(draft.configuration.webhookTemplate || '')" @update:model-value="draft.configuration.webhookTemplate = $event" :aria-label="t('settings.notification.custom_template')" /></NxpField>
      </template>
      <p v-if="error" role="alert" class="error-text">{{ error }}</p>
    </section>
    <template #footer>
      <NxpButton v-if="task" :disabled="busy" @click="test">{{ t('settings.test_notifications') }}</NxpButton>
      <NxpButton :disabled="busy" @click="emit('close')">{{ t('common.cancel') }}</NxpButton>
      <NxpButton class="primary" :disabled="busy" @click="save">{{ t('common.save') }}</NxpButton>
    </template>
  </NxpModal>
</template>

<style scoped>
.notification-editor { display: grid; min-width: 0; gap: var(--space-5); }
.notification-editor > .settings-list { margin: 0; }
.notification-editor > .form-grid { align-items: start; }
.notification-editor > .error-text { margin: 0; }
@media (max-width: 560px) { .notification-editor { gap: var(--space-3); } }
</style>
