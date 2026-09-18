<script setup lang="ts">
import { computed, ref } from "vue";
import { api, isAbortError } from "../../../platform/api";
import { t } from "../../../platform/i18n";
import { toast } from "../../../platform/toast";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpField from "../../../ui/primitives/NxpField.vue";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpNumberInput from "../../../ui/primitives/NxpNumberInput.vue";
import NxpSelect, { type NxpOption } from "../../../ui/primitives/NxpSelect.vue";
import NxpCollapsibleCard from "../../../ui/composites/NxpCollapsibleCard.vue";
import NxpCollapseTransition from "../../../ui/composites/NxpCollapseTransition.vue";
import NxpSwitchSetting from "../../../ui/composites/NxpSwitchSetting.vue";
import NxpTextArea from "../../../ui/primitives/NxpTextArea.vue";
import NxpTextInput from "../../../ui/primitives/NxpTextInput.vue";
import type { Settings } from "../utils/settingsTypes";

/** 通知 section：Webhook/SMTP 表单、密钥草稿、通道开关与测试发送；保存事务由页面统一调度。 */

const props = defineProps<{
  settings: Settings;
  expanded: boolean;
  save: () => void;
  secretDraft: Record<string, string>;
}>();
const emit = defineEmits<{ toggle: [] }>();

const localOpenPanel = ref<"webhook" | "smtp" | null>("webhook");

function toggleLocalPanel(id: "webhook" | "smtp") {
  localOpenPanel.value = localOpenPanel.value === id ? null : id;
}
function notificationEnabled(key: string) {
  return Boolean(props.settings[key]);
}
function testNotifications() {
  void (async () => {
    try {
      const result = (await api("POST", "/api/settings/test")) as { ok?: boolean } | null;
      toast(
        result?.ok ? t("settings.notification.test_success") : t("settings.notification.send_failed"),
        result?.ok ? "info" : "error",
      );
    } catch (reason) {
      if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
    }
  })();
}

const webhookTypeOptions = computed<NxpOption[]>(() => [
  { value: "feishu", label: "Feishu" },
  { value: "dingtalk", label: "Dingtalk" },
  { value: "wecom", label: "WeCom" },
  { value: "slack", label: "Slack" },
  { value: "discord", label: "Discord" },
  { value: "generic", label: "Generic" },
]);
const secureOptions = computed<NxpOption[]>(() =>
  ["auto", "ssl", "starttls", "none"].map((value) => ({ value, label: value.toUpperCase() })),
);
</script>

<template>
  <NxpCollapsibleCard
    data-settings-panel="notifications"
    data-testid="notification-settings"
    panel="notifications"
    panel-id="settings-panel-notifications"
    :title="t('settings.notification_channels')"
    :description="t('settings.notification.channels_help')"
    :expanded="expanded"
    @toggle="emit('toggle')"
  >
      <div class="notification-settings">
        <NxpButton
          class="panel-toggle"
          type="button"
          :aria-expanded="localOpenPanel === 'webhook'"
          @click="toggleLocalPanel('webhook')"
        >
          <span class="panel-arrow"
            ><NxpIcon
              :name="
                localOpenPanel === 'webhook'
                  ? 'chevronDown'
                  : 'chevronRight'
              "
              class-name="panel-arrow-icon" /></span
          ><span class="panel-label"
            >Webhook {{ t("common.notifications") }}</span
          ><NxpBadge
            :tone="notificationEnabled('webhookEnabled') ? 'ok' : 'muted'"
            >{{
              notificationEnabled("webhookEnabled")
                ? t("common.enabled_status")
                : t("common.disabled")
            }}</NxpBadge
          >
        </NxpButton>
        <NxpCollapseTransition>
          <div
            v-show="localOpenPanel === 'webhook'"
            id="panel-wh"
            class="panel-body"
          >
          <div class="settings-list">
            <NxpSwitchSetting
              :label="t('settings.enable_webhook')"
              :description="t('settings.notification.webhook_status')"
              v-model="settings.webhookEnabled"
              :aria-label="t('settings.enable_webhook')"
              @change="save"
            />
            <NxpSwitchSetting
              :label="t('settings.send_screenshots')"
              :description="t('settings.notification.screenshot_scope')"
              v-model="settings.webhookScreenshotEnabled"
              :aria-label="t('settings.send_screenshots')"
              @change="save"
            />
          </div>
          <div class="form-grid">
            <NxpField :label="t('settings.webhook_type')" for="st-whtype-trigger">
              <NxpSelect
                id="st-whtype"
                v-model="settings.webhookType"
                :options="webhookTypeOptions"
                :aria-label="t('settings.webhook_type')"
                @change="save"
              />
            </NxpField>
            <NxpField :label="t('settings.timeout_seconds')" for="st-whtimeout">
              <NxpNumberInput
                id="st-whtimeout"
                v-model.number="settings.webhookTimeout"
                :min="1"
                :aria-label="t('settings.timeout_seconds')"
                @change="save"
              />
            </NxpField>
          </div>
          <div class="form-grid">
            <NxpField :label="t('settings.webhook_address')" for="st-whurl" :help="t('settings.notification.webhook_url_keep')">
              <NxpTextInput
                id="st-whurl"
                v-model="secretDraft.webhookUrl"
                type="url"
                :aria-label="t('settings.webhook_address')"
                :placeholder="
                  settings.webhookUrl
                    ? t('common.leave_blank_to_keep')
                    : 'https://…'
                "
                @blur="save"
              />
            </NxpField>
            <NxpField :label="t('settings.webhook_signing_secret')" for="st-whsec" :help="t('settings.notification.secret_keep')">
              <NxpTextInput
                id="st-whsec"
                v-model="secretDraft.webhookSecret"
                type="password"
                :aria-label="t('settings.webhook_signing_secret')"
                @blur="save"
              />
            </NxpField>
          </div>
          <div v-if="settings.webhookType === 'feishu'" class="webhook-advanced-fields">
            <div class="form-grid">
              <NxpField :label="t('settings.feishu_app_id')" for="st-feishu-appid" :help="t('settings.notification.image_credentials')">
                <NxpTextInput id="st-feishu-appid" v-model="settings.feishuAppId" :aria-label="t('settings.feishu_app_id')" @blur="save" />
              </NxpField>
              <NxpField :label="t('settings.feishu_app_secret', {}, 'Feishu App Secret')" for="st-feishu-secret" :help="t('settings.notification.app_secret_keep')">
                <NxpTextInput
                  id="st-feishu-secret"
                  v-model="secretDraft.feishuAppSecret"
                  type="password"
                  :aria-label="t('settings.feishu_app_secret', {}, 'Feishu App Secret')"
                  :placeholder="settings.feishuAppSecret ? t('common.leave_blank_to_keep') : undefined"
                  autocomplete="new-password"
                  @blur="save"
                />
              </NxpField>
            </div>
          </div>
          <div v-else-if="settings.webhookType === 'slack'" class="webhook-advanced-fields">
            <div class="form-grid">
              <NxpField :label="t('settings.slack_channel_id')" for="st-slack-channel" :help="t('settings.notification.bot_member_help')">
                <NxpTextInput id="st-slack-channel" v-model="settings.slackChannelId" :aria-label="t('settings.slack_channel_id')" @blur="save" />
              </NxpField>
              <NxpField :label="t('settings.slack_bot_token')" for="st-slack-token" :help="t('settings.notification.bot_token_keep')">
                <NxpTextInput
                  id="st-slack-token"
                  v-model="secretDraft.slackBotToken"
                  type="password"
                  :aria-label="t('settings.slack_bot_token')"
                  :placeholder="settings.slackBotToken ? t('common.leave_blank_to_keep') : 'xoxb-…'"
                  autocomplete="new-password"
                  @blur="save"
                />
              </NxpField>
            </div>
          </div>
          <div v-else-if="settings.webhookType === 'dingtalk'" class="webhook-advanced-fields">
            <div class="form-grid">
              <NxpField :label="t('settings.dingtalk_app_key')" for="st-dingtalk-key">
                <NxpTextInput id="st-dingtalk-key" v-model="settings.dingTalkAppKey" :aria-label="t('settings.dingtalk_app_key')" @blur="save" />
              </NxpField>
              <NxpField :label="t('settings.dingtalk_robot_code')" for="st-dingtalk-robot">
                <NxpTextInput id="st-dingtalk-robot" v-model="settings.dingTalkRobotCode" :aria-label="t('settings.dingtalk_robot_code')" @blur="save" />
              </NxpField>
            </div>
            <div class="form-grid">
              <NxpField :label="t('settings.dingtalk_open_conversation_id')" for="st-dingtalk-conversation">
                <NxpTextInput id="st-dingtalk-conversation" v-model="settings.dingTalkOpenConversationId" :aria-label="t('settings.dingtalk_open_conversation_id')" @blur="save" />
              </NxpField>
              <NxpField :label="t('settings.dingtalk_app_secret')" for="st-dingtalk-secret" :help="t('settings.notification.app_secret_keep')">
                <NxpTextInput
                  id="st-dingtalk-secret"
                  v-model="secretDraft.dingTalkAppSecret"
                  type="password"
                  :aria-label="t('settings.dingtalk_app_secret')"
                  :placeholder="settings.dingTalkAppSecret ? t('common.leave_blank_to_keep') : undefined"
                  autocomplete="new-password"
                  @blur="save"
                />
              </NxpField>
            </div>
          </div>
          <NxpField :label="t('settings.notification.custom_template')" for="st-whtpl" :help="t('settings.notification.template_help')">
            <NxpTextArea
              id="st-whtpl"
              v-model="settings.webhookTemplate"
              @blur="save"
            />
          </NxpField>
          </div>
        </NxpCollapseTransition>
        <NxpButton
          class="panel-toggle"
          type="button"
          :aria-expanded="localOpenPanel === 'smtp'"
          @click="toggleLocalPanel('smtp')"
        >
          <span class="panel-arrow"
            ><NxpIcon
              :name="
                localOpenPanel === 'smtp'
                  ? 'chevronDown'
                  : 'chevronRight'
              "
              class-name="panel-arrow-icon" /></span
          ><span class="panel-label"
            >{{ t("settings.notification.smtp") }}</span
          ><NxpBadge
            :tone="notificationEnabled('smtpEnabled') ? 'ok' : 'muted'"
            >{{
              notificationEnabled("smtpEnabled")
                ? t("common.enabled_status")
                : t("common.disabled")
            }}</NxpBadge
          >
        </NxpButton>
        <NxpCollapseTransition>
          <div
            v-show="localOpenPanel === 'smtp'"
            id="panel-smtp"
            class="panel-body"
          >
          <div class="settings-list">
            <NxpSwitchSetting
              :label="t('settings.enable_smtp')"
              :description="t('settings.notification.email_status')"
              v-model="settings.smtpEnabled"
              :aria-label="t('settings.enable_smtp')"
              @change="save"
            />
            <NxpSwitchSetting
              :label="t('settings.send_screenshots')"
              :description="t('settings.notification.screenshot_scope')"
              v-model="settings.smtpScreenshotEnabled"
              :aria-label="t('settings.send_screenshots')"
              @change="save"
            />
          </div>
          <div class="form-grid three">
            <NxpField :label="t('settings.smtp_server')" for="st-host">
              <NxpTextInput
                id="st-host"
                v-model="settings.smtpHost"
                :aria-label="t('settings.smtp_server')"
                @blur="save"
              />
            </NxpField>
            <NxpField :label="t('settings.port')" for="st-port2">
              <NxpNumberInput
                id="st-port2"
                v-model.number="settings.smtpPort"
                :aria-label="t('settings.port')"
                @change="save"
              />
            </NxpField>
            <NxpField :label="t('settings.encryption')" for="st-secure-trigger">
              <NxpSelect
                id="st-secure"
                v-model="settings.smtpSecure"
                :options="secureOptions"
                :aria-label="t('settings.encryption')"
                @change="save"
              />
            </NxpField>
          </div>
          <div class="form-grid">
            <NxpField :label="t('common.account')" for="st-user">
              <NxpTextInput
                id="st-user"
                v-model="settings.smtpUser"
                :aria-label="t('common.account')"
                @blur="save"
              />
            </NxpField>
            <NxpField :label="t('settings.smtp_password')" for="st-pwd">
              <NxpTextInput
                id="st-pwd"
                v-model="secretDraft.smtpPassword"
                type="password"
                :aria-label="t('settings.smtp_password')"
                @blur="save"
              />
            </NxpField>
          </div>
          <div class="form-grid">
            <NxpField :label="t('settings.notification.recipients_help')" for="st-to">
              <NxpTextInput
                id="st-to"
                v-model="settings.smtpTo"
                :aria-label="t('settings.notification.recipients_help')"
                @blur="save"
              />
            </NxpField>
            <NxpField :label="t('settings.from_address_blank_account')" for="st-from">
              <NxpTextInput
                id="st-from"
                v-model="settings.smtpFrom"
                :aria-label="t('settings.from_address_blank_account')"
                @blur="save"
              />
            </NxpField>
          </div>
          <div class="form-grid">
            <NxpField :label="t('settings.subject_prefix')" for="st-subject">
              <NxpTextInput
                id="st-subject"
                v-model="settings.smtpSubjectPrefix"
                :aria-label="t('settings.subject_prefix')"
                @blur="save"
              />
            </NxpField>
            <NxpField :label="t('settings.timeout_seconds')" for="st-smtp-timeout">
              <NxpNumberInput
                id="st-smtp-timeout"
                v-model.number="settings.smtpTimeout"
                :min="1"
                :aria-label="t('settings.timeout_seconds')"
                @change="save"
              />
            </NxpField>
          </div>
          </div>
        </NxpCollapseTransition>
        <div class="modal-footer-inline plain">
          <NxpButton class="ghost" type="button" @click="testNotifications">
            {{ t("settings.test_notifications") }}
          </NxpButton>
        </div>
      </div>
  </NxpCollapsibleCard>
</template>
