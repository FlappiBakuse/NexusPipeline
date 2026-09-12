<script setup lang="ts">
import { computed, ref } from "vue";
import { api, isAbortError } from "../../../platform/api";
import { t } from "../../../platform/i18n";
import { toast } from "../../../platform/toast";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpNumberInput from "../../../ui/primitives/NxpNumberInput.vue";
import NxpSelect, { type NxpOption } from "../../../ui/primitives/NxpSelect.vue";
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
  <section
    class="settings-card section-surface"
    :class="{ 'is-expanded': expanded }"
    data-settings-panel="notifications"
    data-testid="notification-settings"
  >
    <button
      class="settings-card-toggle"
      type="button"
      data-action="toggle-settings-panel"
      data-panel="notifications"
      :aria-expanded="expanded"
      aria-controls="settings-panel-notifications"
      @click.stop="emit('toggle')"
    >
      <span class="settings-card-copy"
        ><strong class="settings-card-title">{{
          t("settings.notification_channels")
        }}</strong
        ><span class="muted">{{
          t("settings.notification.channels_help")
        }}</span></span
      ><span class="settings-card-arrow" aria-hidden="true"
        ><NxpIcon
          :name="
            expanded
              ? 'chevronDown'
              : 'chevronRight'
          "
          class-name="settings-card-arrow-icon"
      /></span>
    </button>
    <NxpCollapseTransition>
      <div
        id="settings-panel-notifications"
        class="settings-card-body"
        v-show="expanded"
      >
      <div class="notification-settings">
        <button
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
        </button>
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
            <div class="field">
              <label class="field-label" for="st-whtype-trigger">{{
                t("settings.webhook_type")
              }}</label
              ><NxpSelect
                id="st-whtype"
                v-model="settings.webhookType"
                :options="webhookTypeOptions"
                :aria-label="t('settings.webhook_type')"
                @change="save"
              />
            </div>
            <div class="field">
              <label class="field-label" for="st-whtimeout">{{
                t("settings.timeout_seconds")
              }}</label
              ><NxpNumberInput
                id="st-whtimeout"
                v-model.number="settings.webhookTimeout"
                :min="1"
                :aria-label="t('settings.timeout_seconds')"
                @change="save"
              />
            </div>
          </div>
          <div class="form-grid">
            <div class="field" :data-help="t('settings.notification.webhook_url_keep')">
              <label class="field-label" for="st-whurl">{{
                t("settings.webhook_address")
              }}</label
              ><NxpTextInput
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
            </div>
            <div class="field" :data-help="t('settings.notification.secret_keep')">
              <label class="field-label" for="st-whsec">{{
                t("settings.webhook_signing_secret")
              }}</label
              ><NxpTextInput
                id="st-whsec"
                v-model="secretDraft.webhookSecret"
                type="password"
                :aria-label="t('settings.webhook_signing_secret')"
                @blur="save"
              />
            </div>
          </div>
          <div v-if="settings.webhookType === 'feishu'" class="webhook-advanced-fields">
            <div class="form-grid">
              <div class="field" :data-help="t('settings.notification.image_credentials')">
                <label class="field-label" for="st-feishu-appid">{{ t("settings.feishu_app_id") }}</label>
                <NxpTextInput id="st-feishu-appid" v-model="settings.feishuAppId" :aria-label="t('settings.feishu_app_id')" @blur="save" />
              </div>
              <div class="field" :data-help="t('settings.notification.app_secret_keep')">
                <label class="field-label" for="st-feishu-secret">{{ t("settings.feishu_app_secret", {}, "Feishu App Secret") }}</label>
                <NxpTextInput
                  id="st-feishu-secret"
                  v-model="secretDraft.feishuAppSecret"
                  type="password"
                  :aria-label="t('settings.feishu_app_secret', {}, 'Feishu App Secret')"
                  :placeholder="settings.feishuAppSecret ? t('common.leave_blank_to_keep') : undefined"
                  autocomplete="new-password"
                  @blur="save"
                />
              </div>
            </div>
          </div>
          <div v-else-if="settings.webhookType === 'slack'" class="webhook-advanced-fields">
            <div class="form-grid">
              <div class="field" :data-help="t('settings.notification.bot_member_help')">
                <label class="field-label" for="st-slack-channel">{{ t("settings.slack_channel_id") }}</label>
                <NxpTextInput id="st-slack-channel" v-model="settings.slackChannelId" :aria-label="t('settings.slack_channel_id')" @blur="save" />
              </div>
              <div class="field" :data-help="t('settings.notification.bot_token_keep')">
                <label class="field-label" for="st-slack-token">{{ t("settings.slack_bot_token") }}</label>
                <NxpTextInput
                  id="st-slack-token"
                  v-model="secretDraft.slackBotToken"
                  type="password"
                  :aria-label="t('settings.slack_bot_token')"
                  :placeholder="settings.slackBotToken ? t('common.leave_blank_to_keep') : 'xoxb-…'"
                  autocomplete="new-password"
                  @blur="save"
                />
              </div>
            </div>
          </div>
          <div v-else-if="settings.webhookType === 'dingtalk'" class="webhook-advanced-fields">
            <div class="form-grid">
              <div class="field">
                <label class="field-label" for="st-dingtalk-key">{{ t("settings.dingtalk_app_key") }}</label>
                <NxpTextInput id="st-dingtalk-key" v-model="settings.dingTalkAppKey" :aria-label="t('settings.dingtalk_app_key')" @blur="save" />
              </div>
              <div class="field">
                <label class="field-label" for="st-dingtalk-robot">{{ t("settings.dingtalk_robot_code") }}</label>
                <NxpTextInput id="st-dingtalk-robot" v-model="settings.dingTalkRobotCode" :aria-label="t('settings.dingtalk_robot_code')" @blur="save" />
              </div>
            </div>
            <div class="form-grid">
              <div class="field">
                <label class="field-label" for="st-dingtalk-conversation">{{ t("settings.dingtalk_open_conversation_id") }}</label>
                <NxpTextInput id="st-dingtalk-conversation" v-model="settings.dingTalkOpenConversationId" :aria-label="t('settings.dingtalk_open_conversation_id')" @blur="save" />
              </div>
              <div class="field" :data-help="t('settings.notification.app_secret_keep')">
                <label class="field-label" for="st-dingtalk-secret">{{ t("settings.dingtalk_app_secret") }}</label>
                <NxpTextInput
                  id="st-dingtalk-secret"
                  v-model="secretDraft.dingTalkAppSecret"
                  type="password"
                  :aria-label="t('settings.dingtalk_app_secret')"
                  :placeholder="settings.dingTalkAppSecret ? t('common.leave_blank_to_keep') : undefined"
                  autocomplete="new-password"
                  @blur="save"
                />
              </div>
            </div>
          </div>
          <div class="field" :data-help="t('settings.notification.template_help')">
            <label class="field-label" for="st-whtpl">{{
              t("settings.notification.custom_template")
            }}</label
            ><NxpTextArea
              id="st-whtpl"
              v-model="settings.webhookTemplate"
              @blur="save"
            />
          </div>
          </div>
        </NxpCollapseTransition>
        <button
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
        </button>
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
            <div class="field">
              <label class="field-label" for="st-host">{{
                t("settings.smtp_server")
              }}</label
              ><NxpTextInput
                id="st-host"
                v-model="settings.smtpHost"
                :aria-label="t('settings.smtp_server')"
                @blur="save"
              />
            </div>
            <div class="field">
              <label class="field-label" for="st-port2">{{
                t("settings.port")
              }}</label
              ><NxpNumberInput
                id="st-port2"
                v-model.number="settings.smtpPort"
                :aria-label="t('settings.port')"
                @change="save"
              />
            </div>
            <div class="field">
              <label class="field-label" for="st-secure-trigger">{{
                t("settings.encryption")
              }}</label
              ><NxpSelect
                id="st-secure"
                v-model="settings.smtpSecure"
                :options="secureOptions"
                :aria-label="t('settings.encryption')"
                @change="save"
              />
            </div>
          </div>
          <div class="form-grid">
            <div class="field">
              <label class="field-label" for="st-user">{{
                t("common.account")
              }}</label
              ><NxpTextInput
                id="st-user"
                v-model="settings.smtpUser"
                :aria-label="t('common.account')"
                @blur="save"
              />
            </div>
            <div class="field">
              <label class="field-label" for="st-pwd">{{
                t("settings.smtp_password")
              }}</label
              ><NxpTextInput
                id="st-pwd"
                v-model="secretDraft.smtpPassword"
                type="password"
                :aria-label="t('settings.smtp_password')"
                @blur="save"
              />
            </div>
          </div>
          <div class="form-grid">
            <div class="field">
              <label class="field-label" for="st-to">{{
                t("settings.notification.recipients_help")
              }}</label
              ><NxpTextInput
                id="st-to"
                v-model="settings.smtpTo"
                :aria-label="t('settings.notification.recipients_help')"
                @blur="save"
              />
            </div>
            <div class="field">
              <label class="field-label" for="st-from">{{
                t("settings.from_address_blank_account")
              }}</label
              ><NxpTextInput
                id="st-from"
                v-model="settings.smtpFrom"
                :aria-label="t('settings.from_address_blank_account')"
                @blur="save"
              />
            </div>
          </div>
          <div class="form-grid">
            <div class="field">
              <label class="field-label" for="st-subject">{{
                t("settings.subject_prefix")
              }}</label
              ><NxpTextInput
                id="st-subject"
                v-model="settings.smtpSubjectPrefix"
                :aria-label="t('settings.subject_prefix')"
                @blur="save"
              />
            </div>
            <div class="field">
              <label class="field-label" for="st-smtp-timeout">{{
                t("settings.timeout_seconds")
              }}</label
              ><NxpNumberInput
                id="st-smtp-timeout"
                v-model.number="settings.smtpTimeout"
                :min="1"
                :aria-label="t('settings.timeout_seconds')"
                @change="save"
              />
            </div>
          </div>
          </div>
        </NxpCollapseTransition>
        <div class="modal-footer-inline plain">
          <NxpButton class="ghost" type="button" @click="testNotifications">
            {{ t("settings.test_notifications") }}
          </NxpButton>
        </div>
      </div>
      </div>
    </NxpCollapseTransition>
  </section>
</template>
