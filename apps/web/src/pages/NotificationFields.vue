<script setup lang="ts">
import type {
  ControlNotificationCommandInput,
  ControlNotificationCommandConfiguration
} from '../control/wire.generated';
import FormNumber from '../components/FormNumber.vue';
import FormSelect from '../components/FormSelect';
import NotificationSecretInput from './NotificationSecretInput.vue';
import type { SecretField } from './notification-types';
import { t } from '../app/locale';

type Input = ControlNotificationCommandInput;
type Configuration = ControlNotificationCommandConfiguration;
const props = defineProps<{
  modelValue: Input;
  disabled: boolean;
  credentialsReady: boolean;
  secrets: Readonly<Record<SecretField, string>>;
  configured: Partial<Record<SecretField, boolean>>;
}>();
const emit = defineEmits<{
  'update:modelValue': [value: Input];
  secret: [field: SecretField, value: string];
}>();
function patch(value: Partial<Input>): void {
  emit('update:modelValue', { ...props.modelValue, ...value });
}
function configure(value: Partial<Configuration>): void {
  patch({ configuration: { ...props.modelValue.configuration, ...value } });
}
function editSecret(field: SecretField, value: string): void {
  emit('secret', field, value);
}
</script>

<template>
  <div class="notification-fields" :inert="disabled">
    <label>
      {{ t('任务名称') }}
      <span class="required">*</span>
      <input
        :value="modelValue.name"
        :aria-label="t('任务名称')"
        maxlength="64"
        required
        @input="patch({ name: ($event.target as HTMLInputElement).value })"
      />
    </label>
    <label>
      {{ t('备注') }}
      <nexus-text-area
        :aria-label="t('备注')"
        :value.prop="modelValue.remark"
        :max-length.prop.camel="512"
        :rows.prop="4"
        @nexus-change="patch({ remark: $event.detail.value })"
      />
    </label>
    <div class="switches">
      <nexus-switch-setting
        :title="t('启用此任务')"
        :description="t('启用后，选择此任务的用户、队列或扩展任务可发送通知；禁用时跳过发送。')"
        :value.prop="modelValue.isEnabled"
        :disabled.prop="disabled"
        @nexus-change="patch({ isEnabled: $event.detail.value })"
      />
      <nexus-switch-setting
        :title="t('发送截图')"
        :description="t('脚本完成通知附带所选截图；队列汇总通知不附图')"
        :value.prop="modelValue.configuration.screenshotEnabled"
        :disabled.prop="disabled"
        @nexus-change="configure({ screenshotEnabled: $event.detail.value })"
      />
    </div>
    <template v-if="modelValue.kind === 'smtp'">
      <div class="columns three">
        <label>
          {{ t('SMTP 服务器') }}
          <input
            :value="modelValue.configuration.smtpHost"
            @input="configure({ smtpHost: ($event.target as HTMLInputElement).value })"
          />
        </label>
        <label>
          {{ t('端口') }}
          <FormNumber
            :model-value="modelValue.configuration.smtpPort"
            :min="1"
            :max="65535"
            @update:model-value="$event !== null && configure({ smtpPort: $event })"
          />
        </label>
        <label>
          {{ t('加密方式') }}
          <FormSelect
            :model-value="modelValue.configuration.smtpSecurity"
            @update:model-value="configure({ smtpSecurity: $event })"
          >
            <option value="auto">AUTO</option>
            <option value="ssl">SSL</option>
            <option value="startTls">STARTTLS</option>
            <option value="none">NONE</option>
          </FormSelect>
        </label>
      </div>
      <div class="columns">
        <label>
          {{ t('账号') }}
          <input
            :value="modelValue.configuration.smtpUser"
            autocomplete="off"
            @input="configure({ smtpUser: ($event.target as HTMLInputElement).value })"
          />
        </label>
        <NotificationSecretInput
          field="smtp-password"
          label="授权码"
          :value="secrets['smtp-password']"
          :configured="!!configured['smtp-password']"
          :disabled="disabled || !credentialsReady"
          @edit="editSecret"
        />
      </div>
      <div class="columns">
        <label>
          {{ t('收件人（逗号分隔）') }}
          <input
            :value="modelValue.configuration.smtpTo"
            @input="configure({ smtpTo: ($event.target as HTMLInputElement).value })"
          />
        </label>
        <label>
          {{ t('发件人显示地址（留空=账号）') }}
          <input
            :value="modelValue.configuration.smtpFrom"
            @input="configure({ smtpFrom: ($event.target as HTMLInputElement).value })"
          />
        </label>
      </div>
      <div class="columns">
        <label>
          {{ t('主题前缀') }}
          <input
            :value="modelValue.configuration.smtpSubjectPrefix"
            @input="configure({ smtpSubjectPrefix: ($event.target as HTMLInputElement).value })"
          />
        </label>
        <label>
          {{ t('超时秒数') }}
          <FormNumber
            :model-value="modelValue.configuration.timeoutSeconds"
            :min="1"
            :max="300"
            @update:model-value="$event !== null && configure({ timeoutSeconds: $event })"
          />
        </label>
      </div>
    </template>
    <template v-else>
      <div class="columns">
        <label>
          {{ t('Webhook 类型') }}
          <FormSelect
            :model-value="modelValue.configuration.webhookKind"
            @update:model-value="configure({ webhookKind: $event })"
          >
            <option value="feishu">Feishu</option>
            <option value="wecom">WeCom</option>
            <option value="slack">Slack</option>
            <option value="discord">Discord</option>
            <option value="generic">Generic</option>
          </FormSelect>
        </label>
        <label>
          {{ t('超时秒数') }}
          <FormNumber
            :model-value="modelValue.configuration.timeoutSeconds"
            :min="1"
            :max="300"
            @update:model-value="$event !== null && configure({ timeoutSeconds: $event })"
          />
        </label>
      </div>
      <div class="columns">
        <NotificationSecretInput
          field="webhook-url"
          label="Webhook 地址"
          :value="secrets['webhook-url']"
          :configured="!!configured['webhook-url']"
          :disabled="disabled || !credentialsReady"
          @edit="editSecret"
        />
        <NotificationSecretInput
          field="webhook-secret"
          label="Webhook 签名密钥"
          :value="secrets['webhook-secret']"
          :configured="!!configured['webhook-secret']"
          :disabled="disabled || !credentialsReady"
          @edit="editSecret"
        />
      </div>
      <div v-if="modelValue.configuration.webhookKind === 'feishu'" class="columns">
        <label>
          {{ t('飞书 App ID') }}
          <input
            :value="modelValue.configuration.feishuAppId"
            @input="configure({ feishuAppId: ($event.target as HTMLInputElement).value })"
          />
        </label>
        <NotificationSecretInput
          field="feishu-app-secret"
          label="飞书 App Secret"
          :value="secrets['feishu-app-secret']"
          :configured="!!configured['feishu-app-secret']"
          :disabled="disabled || !credentialsReady"
          @edit="editSecret"
        />
      </div>
      <div v-if="modelValue.configuration.webhookKind === 'slack'" class="columns">
        <label>
          Slack Channel ID
          <input
            :value="modelValue.configuration.slackChannelId"
            @input="configure({ slackChannelId: ($event.target as HTMLInputElement).value })"
          />
        </label>
        <NotificationSecretInput
          field="slack-bot-token"
          label="Slack Bot Token"
          :value="secrets['slack-bot-token']"
          :configured="!!configured['slack-bot-token']"
          :disabled="disabled || !credentialsReady"
          @edit="editSecret"
        />
      </div>
      <label v-if="modelValue.configuration.webhookKind === 'generic'">
        {{ t('generic 自定义模板（JSON）') }}
        <nexus-text-area
          :aria-label="t('generic 自定义模板（JSON）')"
          :value.prop="modelValue.configuration.webhookTemplate"
          :rows.prop="4"
          @nexus-change="configure({ webhookTemplate: $event.detail.value })"
        />
      </label>
    </template>
  </div>
</template>

<style scoped src="./notification-fields.css"></style>
