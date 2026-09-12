<script setup lang="ts">
import {
  computed,
  nextTick,
  onBeforeUnmount,
  onMounted,
  reactive,
  ref,
} from "vue";
import { api, isAbortError } from "../../platform/api";
import { renderPluginSlot } from "@bridge/index";
import { disposePluginSlot } from "@bridge/index";
import {
  getLocale,
  getLocaleOptions,
  setLocale,
  t,
} from "../../platform/i18n";
import { setTopbarTitle } from "../../platform/shell";
import { useShellStore } from "../../stores/shell";
import { toast } from "../../platform/toast";
import type { NxpOption } from "../../ui/primitives/NxpSelect.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpPageHeader from "../../ui/composites/NxpPageHeader.vue";
import NxpCollapsibleCard from "../../ui/composites/NxpCollapsibleCard.vue";
import UpdateStatusCard from "./components/UpdateStatusCard.vue";
import DiagnosticsSection from "./components/DiagnosticsSection.vue";
import SettingsNotificationsSection from "./components/SettingsNotificationsSection.vue";
import SettingsNetworkSection from "./components/SettingsNetworkSection.vue";
import SettingsServiceSection from "./components/SettingsServiceSection.vue";
import SettingsRemoteAccessSection from "./components/SettingsRemoteAccessSection.vue";
import SettingsUpdatesSection from "./components/SettingsUpdatesSection.vue";

type Settings = Record<string, any>;
type UpdateStatus = Record<string, any>;
type DiagnosticCheck = Record<string, any>;
const settings = reactive<Settings>({});
const remoteAddresses = ref<string[]>([]);
const loaded = ref(false);
const loading = ref(true);
const error = ref("");
const openPanel = ref<string | null>("service");
const shell = useShellStore();
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
const updatesSection = ref<InstanceType<typeof SettingsUpdatesSection> | null>(null);
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
function markRestart() {
  shell.markRestartRequired("settings");
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
    await updatesSection.value?.reload();
  });
}
async function refreshRemote() {
  try {
    const data = await api<any>("GET", "/api/settings");
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
    const result = await api<any>("POST", "/api/settings/test");
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
      <NxpPageHeader
        :eyebrow="t('settings.title', {}, 'System settings')"
        :title="t('shell.settings', {}, 'Settings')"
        :description="t('settings.page.help')"
      />
      <div class="settings-cards" data-testid="settings-cards">
        <SettingsServiceSection
          :settings="settings"
          :expanded="panelExpanded('service')"
          :locale="getLocale()"
          :locale-options="localeOptions"
          :save="saveService"
          :save-with-restart="saveServiceWithRestart"
          :on-lightweight-change="onLightweightChange"
          @toggle="togglePanel('service')"
          @change-locale="changeLocale"
        />

        <SettingsNotificationsSection
          :settings="settings"
          :secret-draft="secretDraft"
          :expanded="panelExpanded('notifications')"
          :save="saveNotifications"
          @toggle="togglePanel('notifications')"
        />
        <SettingsRemoteAccessSection
          v-model:token="token"
          v-model:token-visible="tokenVisible"
          :settings="settings"
          :expanded="panelExpanded('remote-mcp')"
          :remote-addresses="remoteAddresses"
          :save="saveService"
          :save-with-restart="saveServiceWithRestart"
          :on-remote-change="onRemoteChange"
          :on-mcp-change="onMcpChange"
          :generate-token="generateToken"
          :copy-token="copyToken"
          @toggle="togglePanel('remote-mcp')"
        />

        <SettingsNetworkSection
          :settings="settings"
          :secret-draft="secretDraft"
          :expanded="panelExpanded('network')"
          :save="saveNetwork"
          @toggle="togglePanel('network')"
        />
        <SettingsUpdatesSection
          ref="updatesSection"
          :settings="settings"
          :expanded="panelExpanded('updates')"
          :save="saveUpdates"
          :on-update-check="onUpdateCheck"
          @toggle="togglePanel('updates')"
        />

        <div
          class="plugin-slot settings-cards-plugin-slot"
          data-plugin-slot="settings.cards"
          data-plugin-anchor="settings.cards"
          data-plugin-mode="settings"
          hidden
        ></div>

        <NxpCollapsibleCard
          panel-id="diagnostics"
          panel="diagnostics"
          controls-id="settings-panel-diagnostics"
          data-settings-panel="diagnostics"
          data-testid="diagnostics-settings"
          :expanded="panelExpanded('diagnostics')"
          :title="t('settings.system_diagnostics')"
          :description="t('settings.diagnostics.check_help')"
          @toggle="togglePanel('diagnostics')"
        >
          <DiagnosticsSection />
        </NxpCollapsibleCard>
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
