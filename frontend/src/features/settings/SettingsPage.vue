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
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";
import NxpNumberInput from "../../ui/primitives/NxpNumberInput.vue";
import NxpSelect, { type NxpOption } from "../../ui/primitives/NxpSelect.vue";
import NxpSwitch from "../../ui/primitives/NxpSwitch.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpCollapseTransition from "../../ui/composites/NxpCollapseTransition.vue";

type Settings = Record<string, any>;
type UpdateStatus = Record<string, any>;
type DiagnosticCheck = Record<string, any>;
interface DiagnosticsData {
  error?: string;
  overallStatus?: string;
  hostVersion?: string;
  checks?: DiagnosticCheck[];
}

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
const updateStatus = ref<UpdateStatus | null>(null);
const diagnostics = ref<DiagnosticsData | null>(null);
const diagnosticsLoading = ref(false);
const saving = ref(false);
const root = ref<HTMLElement | null>(null);
const settingsPanelToggleEvent = "nxp-settings-panel-toggle";
const settingsPanelStateEvent = "nxp-settings-panel-state";
let disposed = false;
let saveChain: Promise<void> = Promise.resolve();
let updateTimer: ReturnType<typeof setTimeout> | null = null;

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
const webhookTypeOptions = computed<NxpOption[]>(() => [
  { value: "feishu", label: "Feishu" },
  { value: "dingtalk", label: "Dingtalk" },
  { value: "wecom", label: "WeCom" },
  { value: "slack", label: "Slack" },
  { value: "discord", label: "Discord" },
  { value: "generic", label: "Generic" },
]);
const secureOptions = computed<NxpOption[]>(() =>
  ["auto", "ssl", "starttls", "none"].map((value) => ({
    value,
    label: value.toUpperCase(),
  })),
);
const updateChannelOptions = computed<NxpOption[]>(() => [
  { value: "prerelease", label: t("settings.pre_release") },
  { value: "stable", label: t("settings.stable") },
]);
const proxyModeOptions = computed<NxpOption[]>(() => [
  { value: "none", label: t("settings.no_proxy") },
  { value: "system", label: t("settings.use_system_settings") },
  { value: "http", label: t("settings.http_https_proxy") },
]);
const completionActionText = computed(() => {
  const state = updateStatus.value?.state || "idle";
  if (state === "checking") return t("settings.update.checking");
  if (state === "downloading") return t("settings.update.downloading");
  if (state === "ready") return t("settings.update.ready_confirm");
  if (state === "applying") return t("settings.update.applying");
  if (updateStatus.value?.available)
    return t("settings.update.version", {
      version: updateStatus.value.latest || "",
    });
  return updateStatus.value?.checked
    ? t("settings.update.latest")
    : t("settings.update.not_checked");
});
const diagnosticChecks = computed<DiagnosticCheck[]>(() =>
  Array.isArray(diagnostics.value?.checks)
    ? (diagnostics.value.checks as DiagnosticCheck[])
    : [],
);
const diagnosticAttentionCount = computed(
  () =>
    diagnosticChecks.value.filter((item) =>
      ["warn", "fail"].includes(item.status),
    ).length,
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
    await loadUpdateStatus();
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
async function loadUpdateStatus() {
  try {
    updateStatus.value = (await api(
      "GET",
      "/api/update/status",
    )) as UpdateStatus;
    scheduleUpdatePoll();
  } catch {
    updateStatus.value = null;
  }
}
function scheduleUpdatePoll() {
  if (updateTimer) clearTimeout(updateTimer);
  if (disposed) return;
  const state = updateStatus.value?.state || "idle";
  const delay =
    state === "checking" || state === "downloading"
      ? 1000
      : updateStatus.value?.automation?.checkEnabled === true
        ? 60000
        : 0;
  if (!delay) return;
  updateTimer = setTimeout(() => void loadUpdateStatus(), delay);
}
async function checkUpdate() {
  try {
    updateStatus.value = (await api(
      "POST",
      "/api/update/check",
    )) as UpdateStatus;
    toast(
      updateStatus.value?.available
        ? t("settings.update.version_available", {
            version: updateStatus.value.latest || "",
          })
        : t("common.already_up_to_date"),
      "info",
    );
    scheduleUpdatePoll();
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
async function downloadUpdate() {
  try {
    await api("POST", "/api/update/download");
    await loadUpdateStatus();
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
async function cancelUpdate() {
  try {
    await api("POST", "/api/update/cancel");
    toast(t("settings.download_cancelled"));
    await loadUpdateStatus();
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
async function applyUpdate(defer: boolean) {
  if (
    !window.confirm(
      t("settings.update.backup_confirm", {
        action: defer
          ? t("settings.update.apply_next_start")
          : t("settings.update.apply_now"),
        version: updateStatus.value?.latest
          ? ` v${updateStatus.value.latest}`
          : "",
      }),
    )
  )
    return;
  try {
    const result = await api("POST", "/api/update/apply", { defer });
    if (result?.error) {
      toast(result.error, "error");
      return;
    }
    toast(
      result?.deferred
        ? t("settings.update.queued_service_start")
        : t("settings.update_apply_started"),
    );
    await loadUpdateStatus();
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
function diagnosticStatusTone(value?: string): "ok" | "warn" | "bad" | "muted" {
  return value === "pass"
    ? "ok"
    : value === "fail"
      ? "bad"
      : value === "warn"
        ? "warn"
        : "muted";
}
function diagnosticStatusLabel(value?: string) {
  return t(
    `diagnostics.status.${value || "unknown"}`,
    {},
    { pass: "Normal", warn: "Attention", fail: "Failed", skipped: "Skipped" }[
      value || ""
    ] ||
      value ||
      "Unknown",
  );
}
function diagnosticCheckLabel(id?: string) {
  const key = String(id || "")
    .replaceAll(".", "_")
    .replaceAll("-", "_");
  const fallback = String(id || "")
    .split(".")
    .map((part) => part.replaceAll("-", " "))
    .join(" · ");
  return t(
    `diagnostics.check.${key}`,
    {},
    fallback || t("diagnostics.detail", {}, "Diagnostic check"),
  );
}
function diagnosticCategoryLabel(category?: string) {
  const key = String(category || "").trim();
  return key ? t(`diagnostics.category.${key}`, {}, key) : "";
}
function diagnosticText(
  code?: string,
  args?: Record<string, unknown>,
  fallback = "",
) {
  if (!code) return fallback;
  const value = t(code, args || {}, "");
  if (value !== code) return value;
  if (code.includes(".summary."))
    return fallback || t("diagnostics.summary", {}, "Summary");
  if (code.endsWith(".detail")) return t("diagnostics.detail", {}, fallback);
  if (code.endsWith(".remediation"))
    return t("diagnostics.remediation", {}, fallback);
  return fallback;
}
async function loadDiagnostics() {
  diagnosticsLoading.value = true;
  try {
    diagnostics.value = (await api(
      "GET",
      "/api/diagnostics",
    )) as DiagnosticsData;
  } catch (reason) {
    if (!isAbortError(reason)) {
      diagnostics.value = {
        error: reason instanceof Error ? reason.message : String(reason),
      };
    }
  } finally {
    diagnosticsLoading.value = false;
  }
}
async function exportDiagnostics() {
  try {
    const result = await api("POST", "/api/diagnostics/export");
    toast(
      t("settings.diagnostics.exported", {
        path: result?.path || t("settings.generated"),
      }),
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
    void loadUpdateStatus();
    void loadDiagnostics();
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
  if (updateTimer) clearTimeout(updateTimer);
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

        <section
          class="settings-card section-surface"
          :class="{ 'is-expanded': panelExpanded('notifications') }"
          data-settings-panel="notifications"
          data-testid="notification-settings"
        >
          <button
            class="settings-card-toggle"
            type="button"
            data-action="toggle-settings-panel"
            data-panel="notifications"
            :aria-expanded="panelExpanded('notifications')"
            aria-controls="settings-panel-notifications"
            @click.stop="togglePanel('notifications')"
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
                  panelExpanded('notifications')
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
              v-show="panelExpanded('notifications')"
            >
            <div class="notification-settings">
              <button
                class="panel-toggle"
                type="button"
                :aria-expanded="openNotificationPanel === 'webhook'"
                @click="toggleNotificationPanel('webhook')"
              >
                <span class="panel-arrow"
                  ><NxpIcon
                    :name="
                      openNotificationPanel === 'webhook'
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
                  v-show="openNotificationPanel === 'webhook'"
                  id="panel-wh"
                  class="panel-body"
                >
                <div class="settings-list">
                  <div class="switch-row settings-option switch-card">
                    <div>
                      <strong>{{ t("settings.enable_webhook") }}</strong
                      ><span class="muted">{{
                        t("settings.notification.webhook_status")
                      }}</span>
                    </div>
                    <NxpSwitch
                      v-model="settings.webhookEnabled"
                      :aria-label="t('settings.enable_webhook')"
                      @change="saveNotifications"
                    />
                  </div>
                  <div class="switch-row settings-option switch-card">
                    <div>
                      <strong>{{ t("settings.send_screenshots") }}</strong
                      ><span class="muted">{{
                        t("settings.notification.screenshot_scope")
                      }}</span>
                    </div>
                    <NxpSwitch
                      v-model="settings.webhookScreenshotEnabled"
                      :aria-label="t('settings.send_screenshots')"
                      @change="saveNotifications"
                    />
                  </div>
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
                      @change="saveNotifications"
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
                      @change="saveNotifications"
                    />
                  </div>
                </div>
                <div class="form-grid">
                  <div class="field" :data-help="t('settings.notification.webhook_url_keep')">
                    <label class="field-label" for="st-whurl">{{
                      t("settings.webhook_address")
                    }}</label
                    ><input
                      id="st-whurl"
                      v-model="secretDraft.webhookUrl"
                      type="url"
                      :placeholder="
                        settings.webhookUrl
                          ? t('common.leave_blank_to_keep')
                          : 'https://…'
                      "
                      @blur="saveNotifications"
                    />
                  </div>
                  <div class="field" :data-help="t('settings.notification.secret_keep')">
                    <label class="field-label" for="st-whsec">{{
                      t("settings.webhook_signing_secret")
                    }}</label
                    ><input
                      id="st-whsec"
                      v-model="secretDraft.webhookSecret"
                      type="password"
                      @blur="saveNotifications"
                    />
                  </div>
                </div>
                <div v-if="settings.webhookType === 'feishu'" class="webhook-advanced-fields">
                  <div class="form-grid">
                    <div class="field" :data-help="t('settings.notification.image_credentials')">
                      <label class="field-label" for="st-feishu-appid">{{ t("settings.feishu_app_id") }}</label>
                      <input id="st-feishu-appid" v-model="settings.feishuAppId" @blur="saveNotifications" />
                    </div>
                    <div class="field" :data-help="t('settings.notification.app_secret_keep')">
                      <label class="field-label" for="st-feishu-secret">{{ t("settings.feishu_app_secret", {}, "Feishu App Secret") }}</label>
                      <input
                        id="st-feishu-secret"
                        v-model="secretDraft.feishuAppSecret"
                        type="password"
                        :placeholder="settings.feishuAppSecret ? t('common.leave_blank_to_keep') : undefined"
                        autocomplete="new-password"
                        @blur="saveNotifications"
                      />
                    </div>
                  </div>
                </div>
                <div v-else-if="settings.webhookType === 'slack'" class="webhook-advanced-fields">
                  <div class="form-grid">
                    <div class="field" :data-help="t('settings.notification.bot_member_help')">
                      <label class="field-label" for="st-slack-channel">{{ t("settings.slack_channel_id") }}</label>
                      <input id="st-slack-channel" v-model="settings.slackChannelId" @blur="saveNotifications" />
                    </div>
                    <div class="field" :data-help="t('settings.notification.bot_token_keep')">
                      <label class="field-label" for="st-slack-token">{{ t("settings.slack_bot_token") }}</label>
                      <input
                        id="st-slack-token"
                        v-model="secretDraft.slackBotToken"
                        type="password"
                        :placeholder="settings.slackBotToken ? t('common.leave_blank_to_keep') : 'xoxb-…'"
                        autocomplete="new-password"
                        @blur="saveNotifications"
                      />
                    </div>
                  </div>
                </div>
                <div v-else-if="settings.webhookType === 'dingtalk'" class="webhook-advanced-fields">
                  <div class="form-grid">
                    <div class="field">
                      <label class="field-label" for="st-dingtalk-key">{{ t("settings.dingtalk_app_key") }}</label>
                      <input id="st-dingtalk-key" v-model="settings.dingTalkAppKey" @blur="saveNotifications" />
                    </div>
                    <div class="field">
                      <label class="field-label" for="st-dingtalk-robot">{{ t("settings.dingtalk_robot_code") }}</label>
                      <input id="st-dingtalk-robot" v-model="settings.dingTalkRobotCode" @blur="saveNotifications" />
                    </div>
                  </div>
                  <div class="form-grid">
                    <div class="field">
                      <label class="field-label" for="st-dingtalk-conversation">{{ t("settings.dingtalk_open_conversation_id") }}</label>
                      <input id="st-dingtalk-conversation" v-model="settings.dingTalkOpenConversationId" @blur="saveNotifications" />
                    </div>
                    <div class="field" :data-help="t('settings.notification.app_secret_keep')">
                      <label class="field-label" for="st-dingtalk-secret">{{ t("settings.dingtalk_app_secret") }}</label>
                      <input
                        id="st-dingtalk-secret"
                        v-model="secretDraft.dingTalkAppSecret"
                        type="password"
                        :placeholder="settings.dingTalkAppSecret ? t('common.leave_blank_to_keep') : undefined"
                        autocomplete="new-password"
                        @blur="saveNotifications"
                      />
                    </div>
                  </div>
                </div>
                <div class="field" :data-help="t('settings.notification.template_help')">
                  <label class="field-label" for="st-whtpl">{{
                    t("settings.notification.custom_template")
                  }}</label
                  ><textarea
                    id="st-whtpl"
                    v-model="settings.webhookTemplate"
                    @blur="saveNotifications"
                  ></textarea>
                </div>
                </div>
              </NxpCollapseTransition>
              <button
                class="panel-toggle"
                type="button"
                :aria-expanded="openNotificationPanel === 'smtp'"
                @click="toggleNotificationPanel('smtp')"
              >
                <span class="panel-arrow"
                  ><NxpIcon
                    :name="
                      openNotificationPanel === 'smtp'
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
                  v-show="openNotificationPanel === 'smtp'"
                  id="panel-smtp"
                  class="panel-body"
                >
                <div class="settings-list">
                  <div class="switch-row settings-option switch-card">
                    <div>
                      <strong>{{ t("settings.enable_smtp") }}</strong
                      ><span class="muted">{{
                        t("settings.notification.email_status")
                      }}</span>
                    </div>
                    <NxpSwitch
                      v-model="settings.smtpEnabled"
                      :aria-label="t('settings.enable_smtp')"
                      @change="saveNotifications"
                    />
                  </div>
                  <div class="switch-row settings-option switch-card">
                    <div>
                      <strong>{{ t("settings.send_screenshots") }}</strong
                      ><span class="muted">{{
                        t("settings.notification.screenshot_scope")
                      }}</span>
                    </div>
                    <NxpSwitch
                      v-model="settings.smtpScreenshotEnabled"
                      :aria-label="t('settings.send_screenshots')"
                      @change="saveNotifications"
                    />
                  </div>
                </div>
                <div class="form-grid three">
                  <div class="field">
                    <label class="field-label" for="st-host">{{
                      t("settings.smtp_server")
                    }}</label
                    ><input
                      id="st-host"
                      v-model="settings.smtpHost"
                      @blur="saveNotifications"
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
                      @change="saveNotifications"
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
                      @change="saveNotifications"
                    />
                  </div>
                </div>
                <div class="form-grid">
                  <div class="field">
                    <label class="field-label" for="st-user">{{
                      t("common.account")
                    }}</label
                    ><input
                      id="st-user"
                      v-model="settings.smtpUser"
                      @blur="saveNotifications"
                    />
                  </div>
                  <div class="field">
                    <label class="field-label" for="st-pwd">{{
                      t("settings.smtp_password")
                    }}</label
                    ><input
                      id="st-pwd"
                      v-model="secretDraft.smtpPassword"
                      type="password"
                      @blur="saveNotifications"
                    />
                  </div>
                </div>
                <div class="form-grid">
                  <div class="field">
                    <label class="field-label" for="st-to">{{
                      t("settings.notification.recipients_help")
                    }}</label
                    ><input
                      id="st-to"
                      v-model="settings.smtpTo"
                      @blur="saveNotifications"
                    />
                  </div>
                  <div class="field">
                    <label class="field-label" for="st-from">{{
                      t("settings.from_address_blank_account")
                    }}</label
                    ><input
                      id="st-from"
                      v-model="settings.smtpFrom"
                      @blur="saveNotifications"
                    />
                  </div>
                </div>
                <div class="form-grid">
                  <div class="field">
                    <label class="field-label" for="st-subject">{{
                      t("settings.subject_prefix")
                    }}</label
                    ><input
                      id="st-subject"
                      v-model="settings.smtpSubjectPrefix"
                      @blur="saveNotifications"
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
                      @change="saveNotifications"
                    />
                  </div>
                </div>
                </div>
              </NxpCollapseTransition>
              <div class="modal-footer-inline plain">
                <button class="ghost" type="button" @click="testNotifications">
                  {{ t("settings.test_notifications") }}
                </button>
              </div>
            </div>
            </div>
          </NxpCollapseTransition>
        </section>

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

        <section
          class="settings-card section-surface"
          :class="{ 'is-expanded': panelExpanded('network') }"
          data-settings-panel="network"
        >
          <button
            class="settings-card-toggle"
            type="button"
            data-action="toggle-settings-panel"
            data-panel="network"
            :aria-expanded="panelExpanded('network')"
            aria-controls="settings-panel-network"
            @click.stop="togglePanel('network')"
          >
            <span class="settings-card-copy"
              ><strong class="settings-card-title">{{
                t("settings.network_proxy")
              }}</strong
              ><span class="muted">{{
                t("settings.network.external_requests")
              }}</span></span
            ><span class="settings-card-arrow" aria-hidden="true"
              ><NxpIcon
                :name="
                  panelExpanded('network') ? 'chevronDown' : 'chevronRight'
                "
                class-name="settings-card-arrow-icon"
            /></span>
          </button>
          <NxpCollapseTransition>
            <div
              id="settings-panel-network"
              class="settings-card-body"
              v-show="panelExpanded('network')"
            >
            <div class="network-settings" :data-help="t('settings.network_proxy_help')">
              <div class="field">
                <label class="field-label" for="st-proxy-mode-trigger">{{
                  t("settings.proxy_mode")
                }}</label
                ><NxpSelect
                  id="st-proxy-mode"
                  v-model="settings.proxyMode"
                  :options="proxyModeOptions"
                  :aria-label="t('settings.proxy_mode')"
                  @change="saveNetwork"
                />
              </div>
              <div
                v-show="settings.proxyMode === 'http'"
                id="st-proxy-custom"
                class="proxy-custom-fields"
              >
                <div class="field" :data-help="t('settings.validation.proxy_scheme')">
                  <label class="field-label" for="st-proxy-url">{{
                    t("settings.http_https_proxy_address")
                  }}</label
                  ><input
                    id="st-proxy-url"
                    v-model="settings.proxyUrl"
                    placeholder="http://127.0.0.1:7890"
                    @blur="saveNetwork"
                  />
                </div>
                <div class="field">
                  <label class="field-label" for="st-proxy-user">{{
                    t("settings.username_optional")
                  }}</label
                  ><input
                    id="st-proxy-user"
                    v-model="settings.proxyUsername"
                    @blur="saveNetwork"
                  />
                </div>
                <div class="field" :data-help="t('settings.network.proxy_password_keep')">
                  <label class="field-label" for="st-proxy-pwd">{{
                    t("settings.password_optional")
                  }}</label
                  ><input
                    id="st-proxy-pwd"
                    v-model="secretDraft.proxyPassword"
                    type="password"
                    @blur="saveNetwork"
                  />
                </div>
              </div>
            </div>
            </div>
          </NxpCollapseTransition>
        </section>

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
              <div
                id="update-status-box"
                class="update-status"
                data-testid="update-status"
              >
                <div class="detail">
                  <div class="kv">
                    <span class="k">{{ t("common.current_version") }}</span
                    ><span>v{{ updateStatus?.current || "—" }}</span>
                  </div>
                  <div class="kv">
                    <span class="k">{{ t("settings.update_channel") }}</span
                    ><span>{{
                      updateStatus?.channel === "stable"
                        ? t("settings.stable")
                        : t("settings.pre_release")
                    }}</span>
                  </div>
                </div>
                <p class="muted update-state-copy">
                  {{ completionActionText }}
                </p>
                <div v-if="updateStatus?.notes" class="update-notes">
                  {{ String(updateStatus.notes).slice(0, 300) }}
                </div>
                <div
                  v-if="
                    updateStatus?.state === 'downloading' &&
                    typeof updateStatus.progress === 'number'
                  "
                  class="progress-line"
                  role="progressbar"
                  :aria-valuenow="updateStatus.progress"
                  aria-valuemin="0"
                  aria-valuemax="100"
                >
                  <div :style="{ width: `${updateStatus.progress}%` }"></div>
                </div>
                <div class="modal-footer-inline plain update-actions">
                  <button
                    class="ghost"
                    type="button"
                    :disabled="
                      updateStatus?.state === 'checking' ||
                      updateStatus?.state === 'downloading' ||
                      updateStatus?.state === 'ready'
                    "
                    @click="checkUpdate"
                  >
                    {{ t("common.check_for_updates") }}</button
                  ><button
                    v-if="updateStatus?.state === 'downloading'"
                    class="ghost"
                    type="button"
                    @click="cancelUpdate"
                  >
                    {{ t("settings.cancel_download") }}</button
                  ><button
                    v-else-if="
                      updateStatus?.state === 'idle' && updateStatus?.available
                    "
                    class="ghost"
                    type="button"
                    @click="downloadUpdate"
                  >
                    {{ t("common.download_update") }}</button
                  ><button
                    v-if="updateStatus?.state === 'ready'"
                    class="primary"
                    type="button"
                    @click="applyUpdate(false)"
                  >
                    {{ t("common.update_now") }}</button
                  ><button
                    v-if="updateStatus?.state === 'ready'"
                    class="ghost"
                    type="button"
                    @click="applyUpdate(true)"
                  >
                    {{ t("common.update_on_next_startup") }}
                  </button>
                </div>
              </div>
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
            <div class="diagnostics-section">
              <div class="row-actions">
                <button
                  class="ghost"
                  type="button"
                  data-testid="load-diagnostics"
                  :disabled="diagnosticsLoading"
                  @click="loadDiagnostics"
                >
                  {{ t("settings.refresh_diagnostics") }}</button
                ><button
                  class="ghost"
                  type="button"
                  data-testid="export-diagnostics"
                  @click="exportDiagnostics"
                >
                  {{ t("settings.diagnostics.export_help") }}
                </button>
              </div>
              <div
                id="diagnostics-status"
                class="diagnostics-status"
                data-testid="diagnostics-status"
                aria-live="polite"
              >
                <p v-if="diagnosticsLoading" class="muted">
                  {{ t("settings.loading_diagnostics") }}
                </p>
                <p
                  v-else-if="diagnostics?.error"
                  class="callout callout-warning"
                >
                  {{ diagnostics.error }}
                </p>
                <template v-else-if="diagnostics"
                  ><div class="diagnostics-overview">
                    <div class="diagnostics-overview-status">
                      <span class="diagnostics-overview-label">{{
                        t("diagnostics.overall", {}, "Overall status")
                      }}</span
                      ><NxpBadge
                        :tone="
                          diagnosticStatusTone(
                            diagnostics.overallStatus || 'warn',
                          )
                        "
                        >{{
                          diagnosticStatusLabel(
                            diagnostics.overallStatus || "warn",
                          )
                        }}</NxpBadge
                      ><span class="muted"
                        >v{{ diagnostics.hostVersion || "" }}</span
                      >
                    </div>
                    <span class="muted diagnostics-overview-meta">{{
                      diagnosticAttentionCount > 0
                        ? t("settings.diagnostics.overview_attention", {
                            total: diagnosticChecks.length,
                            count: diagnosticAttentionCount,
                          })
                        : t("settings.diagnostics.overview_clear", {
                            total: diagnosticChecks.length,
                            count: 0,
                          })
                    }}</span>
                  </div>
                  <div
                    class="diagnostics-table"
                    role="table"
                    :aria-label="t('settings.diagnostics.system_checks')"
                  >
                    <div class="diagnostics-table-header" role="row">
                      <span role="columnheader">{{ t("common.check") }}</span
                      ><span role="columnheader">{{ t("common.status") }}</span
                      ><span role="columnheader">{{
                        t("settings.diagnostics")
                      }}</span>
                    </div>
                    <div
                      v-for="check in diagnosticChecks"
                      :key="check.id"
                      class="diagnostic-row"
                      role="row"
                      :data-diagnostic-status="check.status || 'unknown'"
                    >
                      <div class="diagnostic-check-name" role="cell">
                        <div class="diagnostic-check-title-line">
                          <strong class="diagnostic-check-title">{{
                            diagnosticCheckLabel(check.id)
                          }}</strong
                          ><NxpBadge tone="muted">{{
                            diagnosticCategoryLabel(check.category)
                          }}</NxpBadge>
                        </div>
                        <span class="muted diagnostic-check-id mono">{{
                          check.id || ""
                        }}</span>
                      </div>
                      <div class="diagnostic-check-status" role="cell">
                        <NxpBadge :tone="diagnosticStatusTone(check.status)">{{
                          diagnosticStatusLabel(check.status)
                        }}</NxpBadge>
                      </div>
                      <div class="diagnostic-check-info" role="cell">
                        <div class="diagnostic-check-summary">
                          <span class="diagnostic-info-label">{{
                            t("diagnostics.summary", {}, "Summary")
                          }}</span
                          ><span>{{
                            diagnosticText(
                              check.summaryCode,
                              check.summaryArgs,
                              diagnosticStatusLabel(check.status),
                            )
                          }}</span>
                        </div>
                        <div
                          v-if="check.detailCode"
                          class="diagnostic-check-detail"
                        >
                          <span class="diagnostic-info-label">{{
                            t("diagnostics.detail", {}, "Details")
                          }}</span
                          ><span>{{
                            diagnosticText(check.detailCode, check.detailArgs)
                          }}</span>
                        </div>
                        <div
                          v-if="check.remediationCode"
                          class="diagnostic-check-remediation"
                        >
                          <span class="diagnostic-info-label">{{
                            t("diagnostics.remediation", {}, "Recommendation")
                          }}</span
                          ><span>{{
                            diagnosticText(
                              check.remediationCode,
                              check.remediationArgs,
                            )
                          }}</span>
                        </div>
                      </div>
                    </div>
                    <div
                      v-if="!diagnosticChecks.length"
                      class="diagnostics-empty"
                      role="row"
                    >
                      {{ t("diagnostics.empty", {}, "No diagnostic results") }}
                    </div>
                  </div></template
                >
              </div>
            </div>
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
