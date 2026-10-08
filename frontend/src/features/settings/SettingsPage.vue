<script setup lang="ts">
import {
  computed,
  nextTick,
  onBeforeUnmount,
  onMounted,
  reactive,
  ref,
  watch,
} from "vue";
import { api, isAbortError } from "../../platform/api";
import {beginPageWrite} from '../../platform/page-writes';
import { renderPluginSlot } from "@bridge/index";
import { disposePluginSlot } from "@bridge/index";
import {
  getLocaleOptions,
  t,
} from "../../platform/i18n";
import { setTopbarTitle } from "../../platform/shell";
import { registerRecoveryDirtyGuard } from "../../platform/service-recovery";
import { useShellStore } from "../../stores/shell";
import { toast } from "../../platform/toast";
import type { NxpOption } from "../../ui/primitives/NxpSelect.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpPageHeader from "../../ui/composites/NxpPageHeader.vue";
import NxpCollapsibleCard from "../../ui/composites/NxpCollapsibleCard.vue";
import NxpSwitchSetting from "../../ui/composites/NxpSwitchSetting.vue";
import DiagnosticsSection from "./components/DiagnosticsSection.vue";
import ServiceRestartNotice from "./components/ServiceRestartNotice.vue";
import SettingsNotificationsSection from "./components/SettingsNotificationsSection.vue";
import SettingsNetworkSection from "./components/SettingsNetworkSection.vue";
import SettingsServiceSection from "./components/SettingsServiceSection.vue";
import SettingsRemoteAccessSection from "./components/SettingsRemoteAccessSection.vue";
import SettingsUpdatesSection from "./components/SettingsUpdatesSection.vue";

type Settings = Record<string, any>;
type DiagnosticCheck = Record<string, any>;
const settings = reactive<Settings>({});
const remoteAddresses = ref<{ internalAddress: string | null; publicAddress: string | null; port: number }>({ internalAddress: null, publicAddress: null, port: 58731 });
const loaded = ref(false);
const loading = ref(true);
const error = ref("");
const openPanel = ref<string | null>("service");
const shell = useShellStore();
const token = ref("");
let savedToken = "";
const tokenClearing = ref(false);
const tokenReading = ref(false);
const tokenUnconfirmed = ref(false);
const tokenError = ref("");
let tokenLoaded = false;
let tokenRevision = 0;
let tokenWriteEpoch = 0;
let tokenReadController: AbortController | null = null;
const secretDraft = reactive<Record<string, string>>({
  webhookUrl: "",
  webhookSecret: "",
  feishuAppSecret: "",
  slackBotToken: "",
  smtpPassword: "",
  proxyPassword: "",
});
const saving = ref(false);
const root = ref<HTMLElement | null>(null);
const pluginCategories = ref<{ id: string; label: string }[]>([]);
const panelOrder = ref<string[]>([]);
const builtInPanels = [
  ["service", "settings.service_behavior"], ["notifications", "settings.notification_channels"],
  ["remote-mcp", "settings.remote_access.mcp"], ["network", "settings.network_proxy"],
  ["advanced", "settings.advanced"], ["updates", "settings.update_settings"],
  ["diagnostics", "settings.system_diagnostics"],
];
const categories = computed(() => {
  const positions = new Map(panelOrder.value.map((id, index) => [id, index]));
  return [
    ...builtInPanels.map(([id, key]) => ({ id, label: t(key) })),
    ...pluginCategories.value,
  ].sort((left, right) => (positions.get(left.id) ?? Infinity) - (positions.get(right.id) ?? Infinity));
});
let pluginCategoryObserver: MutationObserver | null = null;
const updatesSection = ref<InstanceType<typeof SettingsUpdatesSection> | null>(null);
const settingsPanelToggleEvent = "nxp-settings-panel-toggle";
const settingsPanelStateEvent = "nxp-settings-panel-state";
let disposed = false;
let saveChain: Promise<void> = Promise.resolve();
let activeSaveSerial = 0;
const saveErrors = new Map<string, unknown>();
let confirmedAllowConfigRepair = false;
let editSerial = 0;
let confirmedEditSerial = 0;
let unregisterDirtyGuard: (() => void) | null = null;
function markLocalEdit(event?: Event) {
  if (event?.target instanceof HTMLElement && event.target.id === "st-token") return;
  editSerial += 1;
}

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
function applyResponse(data: any, observedTokenRevision = tokenRevision) {
  if (data?.settings) {
    const { accessToken, ...otherSettings } = data.settings;
    Object.assign(settings, otherSettings);
    if (observedTokenRevision === tokenRevision && !tokenClearing.value && !tokenUnconfirmed.value)
      settings.accessToken = accessToken;
    confirmedAllowConfigRepair = data.settings.allowConfigRepair === true;
  }
}
function queueSave(action: () => Promise<void>, key = "settings", confirmEdits = true) {
  let releaseWrite: ()=>void;
  try {releaseWrite=beginPageWrite();}catch(reason){return Promise.reject(reason);}
  const savedSerial = editSerial;
  const pending = saveChain.then(async () => {
    activeSaveSerial = savedSerial;
    await action();
    saveErrors.delete(key);
    if (confirmEdits && editSerial === savedSerial) confirmedEditSerial = savedSerial;
  }).finally(releaseWrite);
  saveChain = pending.catch((reason) => {
    if (key !== "access-token" || token.value.trim()) saveErrors.set(key, reason);
    if (!disposed && !isAbortError(reason))
      toast(reason instanceof Error ? reason.message : String(reason), "error");
  });
  return pending;
}
async function beforeRestart() {
  let pending: Promise<void>;
  do { pending = saveChain; await pending; } while (pending !== saveChain);
  if (saveErrors.size) throw saveErrors.values().next().value;
}
async function persist(
  payload: Settings,
  secretKey?: string,
  secretValue?: string,
) {
  const observedTokenRevision = tokenRevision;
  saving.value = true;
  try {
    const data = await api("PUT", "/api/settings", payload);
    if (editSerial === activeSaveSerial) applyResponse(data, observedTokenRevision);
    if (secretKey && secretValue) {
      const secretData = await api("PUT", "/api/settings", {
        secretKey,
        secretValue,
      });
      if (editSerial === activeSaveSerial) applyResponse(secretData, observedTokenRevision);
    }
  } finally {
    saving.value = false;
  }
}
function servicePayload() {
  return {
    autoStart: settings.autoStart === true,
    openDesktopOnStartup: settings.openDesktopOnStartup === true,
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
  const payload = servicePayload();
  return queueSave(() => persist(payload));
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
      pluginAutoUpdateEnabled: settings.pluginAutoUpdateEnabled === true,
      updateChannel: settings.updateChannel || "prerelease",
      updateSourceUrl: (settings.updateSourceUrl || "").trim(),
    });
    await updatesSection.value?.reload();
  });
}
function saveAdvanced() {
  const value = settings.allowConfigRepair === true;
  return queueSave(async () => {
    try {
      await persist({ allowConfigRepair: value });
    } catch (reason) {
      settings.allowConfigRepair = confirmedAllowConfigRepair;
      throw reason;
    }
  });
}
async function refreshRemote() {
  try {
    const data = await api<any>("GET", "/api/settings");
    applyRemoteAddresses(data);
  } catch {
    // 地址展示失败时保留最近一次成功的结果。
  }
}
function onRemoteChange(value: boolean) {
  markLocalEdit();
  settings.allowRemoteAccess = value;
  markRestart();
  const payload = { allowRemoteAccess: value };
  void queueSave(async () => {
    await persist(payload);
    await refreshRemote();
  }).catch(() => {});
}
function onMcpChange(value: boolean) {
  markLocalEdit();
  settings.mcpEnabled = value;
  markRestart();
  void saveService().catch(() => {});
}
function onUpdateCheck(value: boolean) {
  settings.updateCheckEnabled = value;
  if (!value) settings.updateAutoApplyEnabled = false;
  void saveUpdates();
}
function generateToken() {
  if (tokenClearing.value || tokenUnconfirmed.value) return;
  const bytes = new Uint8Array(24);
  crypto.getRandomValues(bytes);
  token.value = Array.from(bytes, (byte) =>
    byte.toString(16).padStart(2, "0"),
  ).join("");
  toast(t("settings.remote_access.token_generated"));
  void saveToken(token.value).then(() => toast(t("settings.access_token_saved"))).catch(() => {});
}
function saveToken(value: string) {
  if (tokenClearing.value || tokenUnconfirmed.value) return Promise.resolve();
  tokenReadController?.abort();
  tokenReading.value = false;
  const revision = ++tokenRevision;
  const epoch = tokenWriteEpoch;
  tokenError.value = "";
  token.value = value;
  const secret = value.trim();
  if (!secret) {
    saveErrors.delete("access-token");
    return Promise.resolve();
  }
  return queueSave(async () => {
    if (epoch !== tokenWriteEpoch) return;
    saving.value = true;
    try {
      const data = await api<any>("PUT", "/api/settings", { secretKey: "accessToken", secretValue: secret });
      if (localStorage.getItem("nexus-token")) localStorage.setItem("nexus-token", secret);
      if (epoch === tokenWriteEpoch && revision === tokenRevision && !disposed) {
        settings.accessToken = data?.settings?.accessToken || "";
        savedToken = secret;
        tokenLoaded = true;
      }
    } finally {
      saving.value = false;
    }
  }, "access-token", false);
}
function onTokenInput(value: string) { void saveToken(value).catch(() => {}); }
function applyRemoteAddresses(data: any) {
  const remote = data?.status?.remote;
  remoteAddresses.value = {
    internalAddress: typeof remote?.internalAddress === "string" ? remote.internalAddress : null,
    publicAddress: typeof remote?.publicAddress === "string" ? remote.publicAddress : null,
    port: Number(remote?.port) || Number(settings.webPort) || 58731,
  };
}
async function readToken() {
  if (tokenClearing.value || disposed) return;
  tokenReadController?.abort();
  const controller = new AbortController();
  tokenReadController = controller;
  const revision = ++tokenRevision;
  tokenReading.value = true;
  tokenError.value = "";
  try {
    await saveChain;
    if (disposed || controller.signal.aborted || revision !== tokenRevision) return;
    const result = await api<{ configured: boolean; value: string | null }>("GET", "/api/settings/access-token", undefined, controller.signal);
    if (disposed || controller.signal.aborted || revision !== tokenRevision) return;
    if (typeof result?.configured !== "boolean" || (result.configured ? typeof result.value !== "string" : result.value !== null)) throw new Error("access_token_read_failed");
    token.value = result.value || "";
    savedToken = token.value;
    settings.accessToken = result.configured ? "enc:***" : "";
    tokenLoaded = true;
    tokenUnconfirmed.value = false;
    saveErrors.delete("access-token");
  } catch {
    if (!disposed && !controller.signal.aborted && revision === tokenRevision)
      tokenError.value = t("settings.remote_access.read_failed");
  } finally {
    if (revision === tokenRevision) tokenReading.value = false;
  }
}
async function clearToken() {
  if (tokenClearing.value || tokenUnconfirmed.value) return;
  if (!window.confirm(t("settings.remote_access.clear_confirm"))) return;
  tokenClearing.value = true;
  tokenReadController?.abort();
  tokenReading.value = false;
  const revision = ++tokenRevision;
  tokenWriteEpoch++;
  tokenError.value = "";
  try {
    await queueSave(async () => {
      const data = await api<any>("PUT", "/api/settings", { secretKey: "accessToken", secretValue: "" });
      if (data?.ok !== true || data.settings?.accessToken !== "") throw new Error("access_token_clear_unconfirmed");
      localStorage.removeItem("nexus-token");
      if (!disposed && revision === tokenRevision) {
        token.value = "";
        savedToken = "";
        settings.accessToken = "";
        tokenLoaded = true;
        tokenUnconfirmed.value = false;
        saveErrors.delete("access-token");
        toast(t("settings.remote_access.cleared"), "info");
      }
    }, "access-token", false);
  } catch {
    if (!disposed) {
      tokenUnconfirmed.value = true;
      tokenError.value = t("settings.remote_access.clear_unconfirmed");
    }
  } finally {
    if (!disposed) tokenClearing.value = false;
  }
}
watch(() => loaded.value && panelExpanded("remote-mcp"), expanded => {
  if (expanded && !tokenLoaded && !tokenReading.value && !token.value) void readToken();
});
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

function syncPluginCategories() {
  if (disposed) return;
  const ids = new Set(builtInPanels.map(([id]) => id));
  const next: { id: string; label: string }[] = [];
  root.value?.querySelectorAll<HTMLElement>('[data-plugin-slot="settings.cards"] nxp-collapsible-card[data-settings-panel]').forEach(card => {
    const id = card.dataset.settingsPanel?.trim();
    const label = card.title.trim();
    if (!id || !label || ids.has(id)) return;
    ids.add(id);
    next.push({ id, label });
  });
  if (JSON.stringify(next) !== JSON.stringify(pluginCategories.value)) pluginCategories.value = next;
  const order = [...new Set(Array.from(root.value?.querySelectorAll<HTMLElement>("[data-settings-panel]") || [])
    .map(card => card.dataset.settingsPanel?.trim() || "").filter(id => ids.has(id)))];
  if (JSON.stringify(order) !== JSON.stringify(panelOrder.value)) panelOrder.value = order;
  if (openPanel.value && !ids.has(openPanel.value)) {
    openPanel.value = null;
    publishSettingsPanelState();
  }
}

function observePluginCategories() {
  if (disposed) return;
  if (!root.value) return;
  pluginCategoryObserver = new MutationObserver(syncPluginCategories);
  pluginCategoryObserver.observe(root.value, { childList: true, subtree: true, attributes: true, attributeFilter: ["title", "data-settings-panel"] });
  syncPluginCategories();
}

onMounted(async () => {
  disposed = false;
  unregisterDirtyGuard = registerRecoveryDirtyGuard(() =>
    saving.value || editSerial !== confirmedEditSerial
    || Boolean(token.value.trim() && token.value.trim() !== savedToken)
    || Object.values(secretDraft).some(value => value.trim().length > 0));
  window.addEventListener(settingsPanelToggleEvent, handleExternalSettingsPanelToggle);
  setTopbarTitle(t("shell.settings", {}, "Settings"));
  try {
    const data = (await api("GET", "/api/settings")) as {
      settings?: Settings;
      status?: { remote?: { internalAddress?: string | null; publicAddress?: string | null; port?: number } };
    };
    Object.assign(settings, data.settings || {});
    confirmedAllowConfigRepair = data.settings?.allowConfigRepair === true;
    applyRemoteAddresses(data);
    token.value = "";
    loaded.value = true;
    loading.value = false;
    await paintPluginSlots();
    observePluginCategories();
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
  tokenRevision++;
  tokenReadController?.abort();
  token.value = "";
  savedToken = "";
  pluginCategoryObserver?.disconnect();
  pluginCategoryObserver = null;
  unregisterDirtyGuard?.();
  unregisterDirtyGuard = null;
  window.removeEventListener(settingsPanelToggleEvent, handleExternalSettingsPanelToggle);
  void disposePluginSlots();
});
</script>

<template>
  <main id="view" ref="root" class="view-root workspace-page page-settings" data-testid="main-view" @input.capture="markLocalEdit">
    <NxpEmptyState v-if="loading" :title="t('common.loading')" />
    <NxpEmptyState
      v-else-if="error"
      :title="t('settings.error.load')"
      :description="error"
      tone="danger"
    />
    <template v-else-if="loaded">
      <NxpPageHeader
        :eyebrow="t('shell.settings', {}, 'Settings')"
        :title="t('shell.settings', {}, 'Settings')"
        :description="t('settings.page.help')"
      />
      <ServiceRestartNotice :before-restart="beforeRestart" />
      <nav class="settings-categories" :aria-label="t('settings.categories')">
        <button v-for="category in categories" :key="category.id" type="button" :aria-pressed="panelExpanded(category.id)" @click="togglePanel(category.id)">{{ category.label }}</button>
      </nav>
      <div class="settings-cards" data-testid="settings-cards">
        <SettingsServiceSection
          :settings="settings"
          :expanded="panelExpanded('service')"
          :locale-options="localeOptions"
          :save="saveService"
          :save-with-restart="saveServiceWithRestart"
          @toggle="togglePanel('service')"
        />

        <SettingsNotificationsSection
          :settings="settings"
          :secret-draft="secretDraft"
          :expanded="panelExpanded('notifications')"
          :save="saveNotifications"
          @toggle="togglePanel('notifications')"
        />
        <SettingsRemoteAccessSection
          :token="token"
          :token-busy="tokenClearing || tokenReading || tokenUnconfirmed"
          :token-error="tokenError"
          :settings="settings"
          :expanded="panelExpanded('remote-mcp')"
          :remote-addresses="remoteAddresses"
          :save-with-restart="saveServiceWithRestart"
          :on-remote-change="onRemoteChange"
          :on-mcp-change="onMcpChange"
          :generate-token="generateToken"
          :clear-token="clearToken"
          :read-token="readToken"
          @update:token="onTokenInput"
          @toggle="togglePanel('remote-mcp')"
        />

        <SettingsNetworkSection
          :settings="settings"
          :secret-draft="secretDraft"
          :expanded="panelExpanded('network')"
          :save="saveNetwork"
          @toggle="togglePanel('network')"
        />
        <NxpCollapsibleCard
          panel-id="settings-panel-advanced"
          panel="advanced"
          data-settings-panel="advanced"
          :expanded="panelExpanded('advanced')"
          :title="t('settings.advanced')"
          :description="t('settings.advanced_help')"
          @toggle="togglePanel('advanced')"
        >
          <div class="settings-list">
            <NxpSwitchSetting
              id="st-config-repair"
              v-model="settings.allowConfigRepair"
              :label="t('settings.config_repair')"
              :description="t('settings.config_repair_help')"
              :aria-label="t('settings.config_repair')"
              @change="saveAdvanced"
            />
          </div>
        </NxpCollapsibleCard>

        <SettingsUpdatesSection
          ref="updatesSection"
          :settings="settings"
          :expanded="panelExpanded('updates')"
          :save="saveUpdates"
          :on-update-check="onUpdateCheck"
          @toggle="togglePanel('updates')"
          @update:update-auto-apply-enabled="settings.updateAutoApplyEnabled = $event"
          @update:plugin-auto-update-enabled="settings.pluginAutoUpdateEnabled = $event"
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
