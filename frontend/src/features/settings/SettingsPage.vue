<script setup lang="ts">
import {
  computed,
  nextTick,
  onBeforeUnmount,
  onMounted,
  reactive,
  ref,
} from "vue";
import { api, isAbortError } from "@legacy/core/api.js";
import { renderPluginSlot } from "@legacy/core/plugin-slots.js";
import { disposePluginSlot } from "@legacy/core/plugin-runtime.js";
import {
  getLocale,
  getLocaleOptions,
  setLocale,
  t,
} from "@legacy/core/i18n.js";
import { setTopbarTitle, toast } from "@legacy/core/ui.js";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";
import NxpNumberInput from "../../ui/primitives/NxpNumberInput.vue";
import NxpSelect, { type NxpOption } from "../../ui/primitives/NxpSelect.vue";
import NxpSwitch from "../../ui/primitives/NxpSwitch.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpCollapseTransition from "../../ui/composites/NxpCollapseTransition.vue";
import UpdateStatusCard from "./components/UpdateStatusCard.vue";
import DiagnosticsSection from "./components/DiagnosticsSection.vue";
import SettingsNotificationsSection from "./components/SettingsNotificationsSection.vue";
import SettingsNetworkSection from "./components/SettingsNetworkSection.vue";

type Settings = Record<string, any>;
type UpdateStatus = Record<string, any>;
type DiagnosticCheck = Record<string, any>;
const settings = reactive<Settings>({});
const remoteAddresses = ref<string[]>([]);
const loaded = ref(false);
const loading = ref(true);
const error = ref("");
const openPanel = ref<string | null>("service");
const openNotificationPanel = ref<"webhook" | "smtp" | null>("webhook");
const restartRequired = ref(false);
const token = ref("");
const tokenVisible = ref(false);
const secretDraft = reactive<Record<string, string>>({
  webhookUrl: "",
  webhookSecret: "",
  feishuAppSecret: "",
  slackBotToken: "",
  dingTalkAppSecret: "",
  smtpPassword: "",
  proxyPassword: "",
});
const saving = ref(false);
const root = ref<HTMLElement | null>(null);
const updateCard = ref<InstanceType<typeof UpdateStatusCard> | null>(null);
const settingsPanelToggleEvent = "nxp-settings-panel-toggle";
const settingsPanelStateEvent = "nxp-settings-panel-state";
let disposed = false;
let saveChain: Promise<void> = Promise.resolve();

const localeOptions = computed<NxpOption[]>(() =>
  getLocaleOptions().map((item) => ({
    value: item.id,
    label: item.nativeName,
  })),
);
const logLevelOptions = computed<NxpOption[]>(() => [
  { value: "debug", label: t("settings.log_level_debug") },
  { value: "info", label: t("settings.log_level_info") },
  { value: "warn", label: t("settings.log_level_warn") },
  { value: "error", label: t("settings.log_level_error") },
  { value: "fatal", label: t("settings.log_level_fatal") },
]);
const updateChannelOptions = computed<NxpOption[]>(() => [
  { value: "prerelease", label: t("settings.pre_release") },
  { value: "stable", label: t("settings.stable") },
]);
function panelExpanded(id: string) {
  return openPanel.value === id;
}
function publishSettingsPanelState() {
  if (typeof window === "undefined") return;
  window.dispatchEvent(
    new CustomEvent(settingsPanelStateEvent, {
      detail: { panelId: openPanel.value },
    }),
  );
}
function togglePanel(id: string) {
  openPanel.value = openPanel.value === id ? null : id;
  publishSettingsPanelState();
}
function handleExternalSettingsPanelToggle(event: Event) {
  const panelId = (event as CustomEvent<{ panelId?: unknown }>).detail?.panelId;
  if (panelId !== null && typeof panelId !== "string") return;
  openPanel.value = panelId;
  publishSettingsPanelState();
}
function toggleNotificationPanel(id: "webhook" | "smtp") {
  openNotificationPanel.value = openNotificationPanel.value === id ? null : id;
}
function notificationEnabled(key: string) {
  return Boolean(settings[key]);
}
function markRestart() {
  restartRequired.value = true;
}
function applyResponse(data: any) {
  if (data?.settings) Object.assign(settings, data.settings);
}
function queueSave(action: () => Promise<void>) {
  const pending = saveChain.then(action);
  saveChain = pending.catch((reason) => {
    if (!disposed && !isAbortError(reason))
      toast(reason instanceof Error ? reason.message : String(reason), "error");
  });
  return pending;
}
async function persist(
  payload: Settings,
  secretKey?: string,
  secretValue?: string,
) {
  saving.value = true;
  try {
    const data = await api("PUT", "/api/settings", payload);
    applyResponse(data);
    if (secretKey && secretValue) {
      const secretData = await api("PUT", "/api/settings", {
        secretKey,
        secretValue,
      });
      applyResponse(secretData);
    }
  } finally {
    saving.value = false;
  }
}
function servicePayload() {
  return {
    autoStart: settings.autoStart === true,
    lightweightMode: settings.lightweightMode === true,
    autoOpenBrowser: settings.autoOpenBrowser === true,
    historyRetentionDays: Number(settings.historyRetentionDays) || 3,
    webPort: Number(settings.webPort) || 58731,
    mcpEnabled: settings.mcpEnabled === true,
    mcpPort: Number(settings.mcpPort) || 58732,
    logLevel: settings.logLevel || "info",
    hostLocale: settings.hostLocale || "zh-CN",
    allowRemoteAccess: settings.allowRemoteAccess === true,
  };
}
function saveService() {
  const value = token.value.trim();
  return queueSave(() =>
    persist(
      servicePayload(),
      value ? "accessToken" : undefined,
      value || undefined,
    ),
  );
}
function saveServiceWithRestart() {
  markRestart();
  return saveService();
}
function saveNotifications() {
  const payload = {
    webhookEnabled: settings.webhookEnabled === true,
    webhookScreenshotEnabled: settings.webhookScreenshotEnabled === true,
    smtpEnabled: settings.smtpEnabled === true,
    smtpScreenshotEnabled: settings.smtpScreenshotEnabled === true,
    webhookType: settings.webhookType || "generic",
    webhookTimeout: Number(settings.webhookTimeout) || 30,
    webhookTemplate: settings.webhookTemplate || "",
    feishuAppId: settings.feishuAppId || "",
    slackChannelId: settings.slackChannelId || "",
    dingTalkAppKey: settings.dingTalkAppKey || "",
    dingTalkRobotCode: settings.dingTalkRobotCode || "",
    dingTalkOpenConversationId: settings.dingTalkOpenConversationId || "",
    smtpHost: settings.smtpHost || "",
    smtpPort: Number(settings.smtpPort) || 465,
    smtpSecure: settings.smtpSecure || "auto",
    smtpUser: settings.smtpUser || "",
    smtpTo: settings.smtpTo || "",
    smtpFrom: settings.smtpFrom || "",
    smtpSubjectPrefix: settings.smtpSubjectPrefix || "",
    smtpTimeout: Number(settings.smtpTimeout) || 30,
  };
  const secrets = Object.entries(secretDraft).filter(
    ([key, value]) => key !== "proxyPassword" && value.trim(),
  );
  return queueSave(async () => {
    await persist(payload);
    for (const [key, value] of secrets) {
      await persist({}, key, value);
      secretDraft[key] = "";
    }
  });
}
function saveNetwork() {
  const password = secretDraft.proxyPassword.trim();
  return queueSave(async () => {
    await persist({
      proxyMode: settings.proxyMode || "none",
      proxyUrl: (settings.proxyUrl || "").trim(),
      proxyUsername: (settings.proxyUsername || "").trim(),
    });
    if (password) {
      await persist({}, "proxyPassword", password);
      secretDraft.proxyPassword = "";
    }
  });
}
function saveUpdates() {
  return queueSave(async () => {
    await persist({
      updateCheckEnabled: settings.updateCheckEnabled === true,
      updateAutoApplyEnabled:
        settings.updateCheckEnabled === true &&
        settings.updateAutoApplyEnabled === true,
      updateChannel: settings.updateChannel || "prerelease",
      updateSourceUrl: (settings.updateSourceUrl || "").trim(),
    });
    await updateCard.value?.reload();
  });
}
async function refreshRemote() {
  try {
    const data = await api("GET", "/api/settings");
    applyResponse(data);
    remoteAddresses.value = Array.isArray(data?.status?.remote?.lanAddresses)
      ? data.status.remote.lanAddresses
      : [];
  } catch {
    // 地址展示失败时保留最近一次成功的结果。
  }
}
function onRemoteChange(value: boolean) {
  settings.allowRemoteAccess = value;
  markRestart();
  const payload = { ...servicePayload(), allowRemoteAccess: value };
  void queueSave(async () => {
    await persist(payload);
    await refreshRemote();
  });
}
function onMcpChange(value: boolean) {
  settings.mcpEnabled = value;
  markRestart();
  void saveService();
}
function onLightweightChange() {
  void saveServiceWithRestart();
}
function onUpdateCheck(value: boolean) {
  settings.updateCheckEnabled = value;
  if (!value) settings.updateAutoApplyEnabled = false;
  void saveUpdates();
}
function generateToken() {
  const bytes = new Uint8Array(24);
  crypto.getRandomValues(bytes);
  token.value = Array.from(bytes, (byte) =>
    byte.toString(16).padStart(2, "0"),
  ).join("");
  tokenVisible.value = false;
  toast(t("settings.remote_access.token_generated"));
  void saveService().then(() => toast(t("settings.access_token_saved")));
}
async function copyToken() {
  if (!token.value.trim()) {
    toast(t("settings.there_is_no_token_to_copy"), "error");
    return;
  }
  try {
    await navigator.clipboard.writeText(token.value.trim());
    toast(t("settings.access_token_copied"));
  } catch {
    toast(t("settings.remote_access.copy_failed"), "error");
  }
}
async function changeLocale(value: string | string[]) {
  const next = Array.isArray(value) ? value[0] : value;
  if (!next || next === getLocale()) return;
  await setLocale(next);
  location.reload();
}
async function testNotifications() {
  await saveChain;
  try {
    const result = await api("POST", "/api/settings/test");
    toast(
      result?.ok
        ? t("settings.notification.test_success")
        : t("settings.notification.send_failed"),
      result?.ok ? "info" : "error",
    );
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
async function restartService() {
  if (!window.confirm(t("settings.restart_warning"))) return;
  await saveChain;
  try {
    await api("POST", "/api/settings/restart");
    toast(t("settings.service_restarting"));
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
async function paintPluginSlots() {
  await nextTick();
  const slots =
    root.value?.querySelectorAll<HTMLElement>("[data-plugin-slot]") || [];
  for (const slot of slots) {
    const name = slot.dataset.pluginSlot;
    if (name)
      await renderPluginSlot(slot, name, {
        mode: slot.dataset.pluginMode || "settings",
      });
  }
}
async function disposePluginSlots() {
  const slots =
    root.value?.querySelectorAll<HTMLElement>("[data-plugin-slot]") || [];
  for (const slot of slots) await disposePluginSlot(slot);
}

onMounted(async () => {
  disposed = false;
  window.addEventListener(settingsPanelToggleEvent, handleExternalSettingsPanelToggle);
  setTopbarTitle(t("shell.settings", {}, "Settings"));
  try {
    const data = (await api("GET", "/api/settings")) as {
      settings?: Settings;
      status?: { remote?: { lanAddresses?: string[] } };
    };
    Object.assign(settings, data.settings || {});
    remoteAddresses.value = Array.isArray(data.status?.remote?.lanAddresses)
      ? data.status.remote.lanAddresses || []
      : [];
    token.value = "";
    loaded.value = true;
    loading.value = false;
    await paintPluginSlots();
    publishSettingsPanelState();
  } catch (reason) {
    if (!isAbortError(reason)) {
      loading.value = false;
      error.value = reason instanceof Error ? reason.message : String(reason);
    }
  }
});
onBeforeUnmount(() => {
  disposed = true;
  window.removeEventListener(settingsPanelToggleEvent, handleExternalSettingsPanelToggle);
  void disposePluginSlots();
});
</script>

<template>
  <main id="view" ref="root" class="view-root" data-testid="main-view">
    <NxpEmptyState v-if="loading" :title="t('common.loading')" />
    <NxpEmptyState
      v-else-if="error"
      :title="t('settings.error.load')"
      :description="error"
      tone="danger"
    />
    <template v-else-if="loaded">
      <header class="page-head">
        <div class="page-head-copy">
          <div class="eyebrow">
            {{ t("settings.title", {}, "System settings") }}
          </div>
          <h2>{{ t("shell.settings", {}, "Settings") }}</h2>
          <p class="page-kicker">{{ t("settings.page.help") }}</p>
        </div>
      </header>
      <section
        v-if="restartRequired"
        id="restart-notice"
        class="dashboard-system-note"
        role="status"
        aria-live="polite"
      >
        <p>{{ t("settings.service.restart_saved") }}</p>
        <span v-if="settings.lightweightMode" class="muted">{{
          t("settings.service.lightweight_restart")
        }}</span
        ><button
          v-else
          class="primary"
          type="button"
          data-testid="restart-service"
          @click="restartService"
        >
          {{ t("settings.restart_service") }}
        </button>
      </section>
      <div class="settings-cards" data-testid="settings-cards">
        <section
          class="settings-card section-surface"
          :class="{ 'is-expanded': panelExpanded('service') }"
          data-settings-panel="service"
          data-testid="service-settings"
        >
          <button
            class="settings-card-toggle"
            type="button"
            data-action="toggle-settings-panel"
            data-panel="service"
            :aria-expanded="panelExpanded('service')"
            aria-controls="settings-panel-service"
            @click.stop="togglePanel('service')"
          >
            <span class="settings-card-copy"
              ><strong class="settings-card-title">{{
                t("settings.service_behavior")
              }}</strong
              ><span class="muted">{{
                t("settings.service.options_help")
              }}</span></span
            ><span class="settings-card-arrow" aria-hidden="true"
              ><NxpIcon
                :name="
                  panelExpanded('service') ? 'chevronDown' : 'chevronRight'
                "
                class-name="settings-card-arrow-icon"
            /></span>
          </button>
          <NxpCollapseTransition>
            <div
              id="settings-panel-service"
              class="settings-card-body"
              v-show="panelExpanded('service')"
            >
            <div class="settings-list">
              <div class="switch-row settings-option switch-card">
                <div>
                  <strong>{{ t("settings.start_with_windows") }}</strong
                  ><span class="muted">{{
                    t("settings.service.startup_registration")
                  }}</span>
                </div>
                <NxpSwitch
                  id="st-autostart"
                  v-model="settings.autoStart"
                  :aria-label="t('settings.start_with_windows')"
                  @change="saveService"
                />
              </div>
              <div class="switch-row settings-option switch-card">
                <div>
                  <strong>{{ t("settings.lightweight_mode") }}</strong
                  ><span class="muted">{{
                    t("settings.service.web_disabled")
                  }}</span>
                </div>
                <NxpSwitch
                  id="st-lightweight"
                  v-model="settings.lightweightMode"
                  :aria-label="t('settings.lightweight_mode')"
                  @change="onLightweightChange"
                />
              </div>
              <div class="switch-row settings-option switch-card">
                <div>
                  <strong>{{ t("settings.open_browser") }}</strong
                  ><span class="muted">{{
                    t("settings.service.console_startup")
                  }}</span>
                </div>
                <NxpSwitch
                  id="st-browser"
                  v-model="settings.autoOpenBrowser"
                  :aria-label="t('settings.open_browser')"
                  @change="saveService"
                />
              </div>
            </div>
            <div
              class="settings-service-fields"
              :data-help="t('settings.service.restart_requirements')"
            >
              <div
                class="form-grid settings-service-grid settings-service-grid-primary"
              >
                <div class="field">
                  <label class="field-label" for="st-retention">{{
                    t("settings.history_retention_days")
                  }}</label
                  ><NxpNumberInput
                    id="st-retention"
                    v-model.number="settings.historyRetentionDays"
                    :min="1"
                    :max="180"
                    :aria-label="t('settings.history_retention_days')"
                    @change="saveService"
                  />
                </div>
                <div class="field">
                  <label class="field-label" for="st-port">{{
                    t("settings.web_port")
                  }}</label
                  ><NxpNumberInput
                    id="st-port"
                    v-model.number="settings.webPort"
                    :min="1024"
                    :max="65535"
                    :aria-label="t('settings.web_port')"
                    @change="saveServiceWithRestart"
                  />
                </div>
                <div class="field">
                  <label class="field-label" for="st-loglevel-trigger">{{
                    t("settings.log_level")
                  }}</label
                  ><NxpSelect
                    id="st-loglevel"
                    v-model="settings.logLevel"
                    :options="logLevelOptions"
                    :aria-label="t('settings.log_level')"
                    @change="saveService"
                  />
                </div>
              </div>
              <div
                class="form-grid settings-service-grid settings-service-grid-locale"
              >
                <div class="field" :data-help="t('settings.language_help', {}, 'Language preference is stored in this browser only')">
                  <label class="field-label" for="settings-locale-trigger">{{
                    t("settings.language", {}, "Interface language")
                  }}</label
                  ><NxpSelect
                    id="settings-locale"
                    :model-value="getLocale()"
                    :options="localeOptions"
                    :aria-label="t('settings.language')"
                    @change="changeLocale"
                  />
                </div>
                <div class="field" :data-help="t('settings.host_language_help', {}, 'Controls CLI, tray, notifications, and background logs.')">
                  <label class="field-label" for="st-host-locale-trigger">{{
                    t("settings.host_language", {}, "Host language")
                  }}</label
                  ><NxpSelect
                    id="st-host-locale"
                    v-model="settings.hostLocale"
                    :options="localeOptions"
                    :aria-label="t('settings.host_language')"
                    @change="saveService"
                  />
                </div>
              </div>
            </div>
            <p v-if="settings.lightweightMode" class="callout callout-warning">
              {{ t("settings.service.lightweight_not_started") }}
            </p>
            </div>
          </NxpCollapseTransition>
        </section>

        <SettingsNotificationsSection
          :settings="settings"
          :secret-draft="secretDraft"
          :expanded="panelExpanded('notifications')"
          :save="saveNotifications"
          @toggle="togglePanel('notifications')"
        />
        <section
          class="settings-card section-surface"
          :class="{ 'is-expanded': panelExpanded('remote-mcp') }"
          data-settings-panel="remote-mcp"
          data-testid="mcp-settings"
        >
          <button
            class="settings-card-toggle"
            type="button"
            data-action="toggle-settings-panel"
            data-panel="remote-mcp"
            :aria-expanded="panelExpanded('remote-mcp')"
            aria-controls="settings-panel-remote-mcp"
            @click.stop="togglePanel('remote-mcp')"
          >
            <span class="settings-card-copy"
              ><strong class="settings-card-title">{{
                t("settings.remote_access.mcp")
              }}</strong
              ><span class="muted">{{
                t("settings.remote_access.management_help")
              }}</span></span
            ><span class="settings-card-arrow" aria-hidden="true"
              ><NxpIcon
                :name="
                  panelExpanded('remote-mcp') ? 'chevronDown' : 'chevronRight'
                "
                class-name="settings-card-arrow-icon"
            /></span>
          </button>
          <NxpCollapseTransition>
            <div
              id="settings-panel-remote-mcp"
              class="settings-card-body"
              v-show="panelExpanded('remote-mcp')"
            >
            <div class="settings-merged-content">
            <section class="settings-subsection remote-settings">
              <div class="settings-list">
                <div class="switch-row settings-option switch-card">
                  <div>
                    <strong>{{ t("settings.remote_access") }}</strong
                    ><span class="muted">{{
                      t("settings.remote_access.bind_all_help")
                    }}</span>
                  </div>
                  <NxpSwitch
                    id="st-remote"
                    :model-value="settings.allowRemoteAccess === true"
                    :aria-label="t('settings.remote_access')"
                    @update:model-value="onRemoteChange"
                  />
                </div>
              </div>
              <div class="field-btn-row">
                <div class="field" :data-help="t('settings.remote_access.token_keep_help')">
                  <label class="field-label" for="st-token">{{
                    t("settings.access_token")
                  }}</label
                  ><input
                    id="st-token"
                    v-model="token"
                    :type="tokenVisible ? 'text' : 'password'"
                    autocomplete="new-password"
                    :placeholder="t('common.leave_blank_to_keep')"
                    @change="saveService"
                  />
                </div>
                <button
                  class="ghost"
                  type="button"
                  data-testid="toggle-token-visibility"
                  :aria-pressed="tokenVisible"
                  @click="tokenVisible = !tokenVisible"
                >
                  {{
                    tokenVisible ? t("settings.hide") : t("common.show")
                  }}</button
                ><button class="ghost" type="button" @click="copyToken">
                  {{ t("settings.copy") }}</button
                ><button
                  class="ghost"
                  type="button"
                  data-testid="gen-token"
                  @click="generateToken"
                >
                  {{ t("settings.generate_token") }}
                </button>
              </div>
              <div
                id="remote-lan-list"
                class="detail"
                :data-help="
                  settings.allowRemoteAccess
                    ? t('settings.remote_access.lan_help_short')
                    : undefined
                "
              >
                <div
                  v-for="address in remoteAddresses"
                  :key="address"
                  class="kv"
                >
                  <span class="k">{{ t("settings.lan_address") }}</span
                  ><span>http://{{ address }}:{{ settings.webPort }}/</span>
                </div>
              </div>
              <p class="callout callout-warning">
                {{ t("settings.remote_access.warning") }}
              </p>
            </section>
            <section class="settings-subsection mcp-settings">
              <div class="settings-list">
                <div class="switch-row settings-option switch-card">
                  <div>
                    <strong>{{ t("settings.enable_mcp_service") }}</strong
                    ><span class="muted">{{
                      t("settings.remote_access.mcp_listen_help")
                    }}</span>
                  </div>
                  <NxpSwitch
                    id="st-mcp-enabled"
                    v-model="settings.mcpEnabled"
                    :aria-label="t('settings.enable_mcp_service')"
                    @change="onMcpChange"
                  />
                </div>
              </div>
              <div
                class="form-grid settings-single-field"
                :data-help="t('settings.remote_access.mcp_endpoint_help', { port: Number(settings.mcpPort) || 58732 })"
              >
                <div class="field">
                  <label class="field-label" for="st-mcp-port">{{
                    t("settings.mcp_port")
                  }}</label
                  ><NxpNumberInput
                    id="st-mcp-port"
                    v-model.number="settings.mcpPort"
                    :min="1024"
                    :max="65535"
                    :aria-label="t('settings.mcp_port')"
                    @change="saveServiceWithRestart"
                  />
                </div>
              </div>
            </section>
            </div>
            </div>
          </NxpCollapseTransition>
        </section>

        <SettingsNetworkSection
          :settings="settings"
          :secret-draft="secretDraft"
          :expanded="panelExpanded('network')"
          :save="saveNetwork"
          @toggle="togglePanel('network')"
        />
        <section
          class="settings-card section-surface"
          :class="{ 'is-expanded': panelExpanded('updates') }"
          data-settings-panel="updates"
          data-testid="update-section"
        >
          <button
            class="settings-card-toggle"
            type="button"
            data-action="toggle-settings-panel"
            data-panel="updates"
            :aria-expanded="panelExpanded('updates')"
            aria-controls="settings-panel-updates"
            @click.stop="togglePanel('updates')"
          >
            <span class="settings-card-copy"
              ><strong class="settings-card-title">{{
                t("settings.update_settings")
              }}</strong
              ><span class="muted">{{
                t("settings.update.channel_help")
              }}</span></span
            ><span class="settings-card-arrow" aria-hidden="true"
              ><NxpIcon
                :name="
                  panelExpanded('updates') ? 'chevronDown' : 'chevronRight'
                "
                class-name="settings-card-arrow-icon"
            /></span>
          </button>
          <NxpCollapseTransition>
            <div
              id="settings-panel-updates"
              class="settings-card-body"
              v-show="panelExpanded('updates')"
            >
            <div class="update-section">
              <div class="settings-list">
                <div class="switch-row settings-option switch-card">
                  <div>
                    <strong>{{ t("settings.update.periodic") }}</strong
                    ><span class="muted">{{
                      t("settings.update.check_schedule")
                    }}</span>
                  </div>
                  <NxpSwitch
                    id="st-update-check"
                    v-model="settings.updateCheckEnabled"
                    :aria-label="t('settings.update.periodic')"
                    @change="onUpdateCheck"
                  />
                </div>
                <div class="switch-row settings-option switch-card">
                  <div>
                    <strong>{{
                      t("settings.update_automatically_when_idle")
                    }}</strong
                    ><span class="muted">{{
                      t("settings.update.auto_download_help")
                    }}</span>
                  </div>
                  <NxpSwitch
                    id="st-update-auto"
                    v-model="settings.updateAutoApplyEnabled"
                    :disabled="settings.updateCheckEnabled !== true"
                    :aria-label="t('settings.update_automatically_when_idle')"
                    @change="saveUpdates"
                  />
                </div>
              </div>
              <div class="form-grid">
                <div class="field">
                  <label class="field-label" for="st-update-channel-trigger">{{
                    t("settings.update_channel")
                  }}</label
                  ><NxpSelect
                    id="st-update-channel"
                    v-model="settings.updateChannel"
                    :options="updateChannelOptions"
                    :aria-label="t('settings.update_channel')"
                    @change="saveUpdates"
                  />
                </div>
                <div class="field" :data-help="t('settings.update.source_default_help')">
                  <label class="field-label" for="st-update-source">{{
                    t("settings.mirror_url")
                  }}</label
                  ><input
                    id="st-update-source"
                    v-model="settings.updateSourceUrl"
                    :placeholder="t('settings.default_github')"
                    @blur="saveUpdates"
                  />
                </div>
              </div>
              <UpdateStatusCard ref="updateCard" />
            </div>
            </div>
          </NxpCollapseTransition>
        </section>

        <div
          class="plugin-slot settings-cards-plugin-slot"
          data-plugin-slot="settings.cards"
          data-plugin-anchor="settings.cards"
          data-plugin-mode="settings"
          hidden
        ></div>

        <section
          class="settings-card section-surface"
          :class="{ 'is-expanded': panelExpanded('diagnostics') }"
          data-settings-panel="diagnostics"
          data-testid="diagnostics-settings"
        >
          <button
            class="settings-card-toggle"
            type="button"
            data-action="toggle-settings-panel"
            data-panel="diagnostics"
            :aria-expanded="panelExpanded('diagnostics')"
            aria-controls="settings-panel-diagnostics"
            @click.stop="togglePanel('diagnostics')"
          >
            <span class="settings-card-copy"
              ><strong class="settings-card-title">{{
                t("settings.system_diagnostics")
              }}</strong
              ><span class="muted">{{
                t("settings.diagnostics.check_help")
              }}</span></span
            ><span class="settings-card-arrow" aria-hidden="true"
              ><NxpIcon
                :name="
                  panelExpanded('diagnostics') ? 'chevronDown' : 'chevronRight'
                "
                class-name="settings-card-arrow-icon"
            /></span>
          </button>
          <NxpCollapseTransition>
            <div
              id="settings-panel-diagnostics"
              class="settings-card-body"
              v-show="panelExpanded('diagnostics')"
            >
            <DiagnosticsSection />
            </div>
          </NxpCollapseTransition>
        </section>
      </div>
      <div
        class="plugin-slot settings-plugin-slot"
        data-plugin-slot="settings.sections"
        data-plugin-anchor="settings.sections"
        data-plugin-mode="settings"
        hidden
      ></div>
    </template>
  </main>
</template>
