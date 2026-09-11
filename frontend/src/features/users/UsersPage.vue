<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { api, isAbortError } from "@legacy/core/api.js";
import { renderPluginSlot } from "@legacy/core/plugin-slots.js";
import { disposePluginSlot } from "@legacy/core/plugin-runtime.js";
import { t } from "@legacy/core/i18n.js";
import { setTopbarTitle, toast } from "@legacy/core/ui.js";
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";
import NxpSelect, { type NxpOption } from "../../ui/primitives/NxpSelect.vue";
import NxpSwitch from "../../ui/primitives/NxpSwitch.vue";

interface BindingEffective {
  enabled?: boolean;
  notifyEnabled?: boolean;
  smtpTo?: string;
  preRunScript?: string;
  postRunScript?: string;
  runDays?: number;
  maxSuccessfulRunsPerDay?: number;
}

interface BindingLocks {
  general?: boolean;
  notification?: boolean;
  advanced?: boolean;
}

interface Binding {
  scriptInstanceId: string;
  scriptName?: string;
  enabled?: boolean;
  notifyEnabled?: boolean;
  smtpTo?: string;
  preRunScript?: string;
  preRunOnceOnly?: boolean;
  postRunScript?: string;
  postRunOnFinalOnly?: boolean;
  runDays?: number;
  maxSuccessfulRunsPerDay?: number;
  configInputs?: Record<string, unknown>;
  effective?: BindingEffective;
  locks?: BindingLocks;
}

interface User {
  id: string;
  index?: number;
  name: string;
  remark?: string;
  avatarUrl?: string;
  bindingCount?: number;
  nextRunAt?: string;
  nextQueueName?: string;
  bindings?: Binding[];
}

interface Script {
  id: string;
  name: string;
  pluginType?: string;
}
interface Plugin {
  name?: string;
  displayName?: string;
  kind?: string;
  configuredEnabled?: boolean;
  runtimeEnabled?: boolean;
  state?: string;
}
interface Badge {
  pluginName?: string;
  id?: string;
  label?: string;
  tone?: string;
  title?: string;
}
interface UserBadges {
  userId?: string;
  badges?: Badge[];
}

interface GlobalField {
  key: string;
  label: string;
  type?: string;
  description?: string;
  placeholder?: string;
  options?: Array<string | { value?: string; label?: string }>;
  required?: boolean;
  readOnly?: boolean;
  maxLength?: number;
}

interface Contribution {
  pluginName?: string;
  pluginDisplayName?: string;
  id?: string;
  title?: string;
  description?: string;
  fields?: GlobalField[];
  values?: Record<string, unknown>;
}

interface GlobalSettings {
  general: {
    syncEnabled: boolean;
    enabled: boolean;
    runDays: number;
    maxSuccessfulRunsPerDay: number;
  };
  notification: {
    syncEnabled: boolean;
    notifyEnabled: boolean;
    smtpTo: string;
  };
  advanced: {
    syncEnabled: boolean;
    preRunScript: string;
    preRunOnceOnly: boolean;
    postRunScript: string;
    postRunOnFinalOnly: boolean;
  };
}

interface GlobalDraft {
  userId: string;
  settings: GlobalSettings;
  contributions: Contribution[];
}

const users = ref<User[]>([]);
const scripts = ref<Script[]>([]);
const plugins = ref<Plugin[]>([]);
const badgesByUser = ref(new Map<string, Badge[]>());
const countdownByUser = ref<Record<string, string>>({});
const loading = ref(true);
const error = ref("");
const newUserOpen = ref(false);
const newUserName = ref("");
const newUserRemark = ref("");
const globalDraft = ref<GlobalDraft | null>(null);
const userDraft = ref<User | null>(null);
const expandedBindingId = ref<string | null>(null);
const bindingEditMode = ref(false);
const addBindingOpen = ref(false);
const selectedBindingIds = ref<string[]>([]);
const deleteTarget = ref<User | null>(null);
const deleteName = ref("");
const root = ref<HTMLElement | null>(null);
let disposed = false;
let countdownTimer: ReturnType<typeof setInterval> | null = null;

const sortedUsers = computed(() =>
  users.value.slice().sort((a, b) => (a.index ?? 0) - (b.index ?? 0)),
);
const availableBindingScripts = computed(() => {
  const bound = new Set(
    (userDraft.value?.bindings || []).map(
      (binding) => binding.scriptInstanceId,
    ),
  );
  return scripts.value.filter((script) => !bound.has(script.id));
});

const clone = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
const errorText = (reason: unknown) =>
  reason instanceof Error ? reason.message : String(reason);

function normalizeGlobalSettings(value: any): GlobalSettings {
  const general = value?.general || {};
  const notification = value?.notification || {};
  const advanced = value?.advanced || {};
  return {
    general: {
      syncEnabled: general.syncEnabled === true,
      enabled: general.enabled !== false,
      runDays: typeof general.runDays === "number" ? general.runDays : -1,
      maxSuccessfulRunsPerDay:
        typeof general.maxSuccessfulRunsPerDay === "number"
          ? general.maxSuccessfulRunsPerDay
          : -1,
    },
    notification: {
      syncEnabled: notification.syncEnabled === true,
      notifyEnabled: notification.notifyEnabled !== false,
      smtpTo: String(notification.smtpTo || ""),
    },
    advanced: {
      syncEnabled: advanced.syncEnabled === true,
      preRunScript: String(advanced.preRunScript || ""),
      preRunOnceOnly: advanced.preRunOnceOnly === true,
      postRunScript: String(advanced.postRunScript || ""),
      postRunOnFinalOnly: advanced.postRunOnFinalOnly === true,
    },
  };
}

function initials(name: string) {
  const chars = Array.from(String(name || t("common.user")).trim());
  if (!chars.length) return t("users.user_initial");
  return /[\u3400-\u9fff]/.test(chars[0])
    ? chars[0]
    : chars.slice(0, 2).join("").toUpperCase();
}

function remainingLabel(value: string) {
  if (!value) return t("users.no_scheduled_tasks");
  const target = new Date(value).getTime();
  if (!Number.isFinite(target)) return t("users.no_scheduled_tasks");
  const remaining = target - Date.now();
  if (remaining <= 0) return t("common.about_to_run");
  const seconds = Math.floor(remaining / 1000);
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const clock = `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
  return days
    ? t("users.schedule.runs_in_days", { days, clock })
    : t("users.schedule.runs_in", { clock });
}

function refreshCountdowns() {
  const next: Record<string, string> = {};
  for (const user of users.value)
    next[user.id] = remainingLabel(user.nextRunAt || "");
  countdownByUser.value = next;
}

async function load() {
  loading.value = true;
  error.value = "";
  try {
    const [userData, scriptData, status, badgeData] = (await Promise.all([
      api("GET", "/api/users"),
      api("GET", "/api/scripts"),
      api("GET", "/api/status"),
      api("GET", "/api/plugin-contributions/user-list-badges"),
    ])) as [User[], Script[], { plugins?: Plugin[] }, UserBadges[]];
    if (disposed) return;
    users.value = Array.isArray(userData) ? userData : [];
    scripts.value = Array.isArray(scriptData) ? scriptData : [];
    plugins.value = Array.isArray(status?.plugins) ? status.plugins : [];
    badgesByUser.value = new Map(
      (Array.isArray(badgeData) ? badgeData : []).map((item) => [
        String(item?.userId || ""),
        Array.isArray(item?.badges) ? item.badges : [],
      ]),
    );
    refreshCountdowns();
    // 插件 slot 属于页面增强，不能阻塞用户列表首屏；老服务端或单个插件异常时，
    // 列表仍必须先可见，slot 在后台完成渲染即可。
    void paintListSlots();
  } catch (reason) {
    if (!disposed && !isAbortError(reason)) error.value = errorText(reason);
  } finally {
    if (!disposed) loading.value = false;
  }
}

function openNewUser() {
  newUserName.value = "";
  newUserRemark.value = "";
  newUserOpen.value = true;
}

function closeNewUser() {
  newUserOpen.value = false;
}

async function createUser() {
  const name = newUserName.value.trim();
  if (!name) {
    toast(t("users.validation.username_required"), "error");
    return;
  }
  try {
    await api("POST", "/api/users", {
      name,
      remark: newUserRemark.value.trim(),
    });
    closeNewUser();
    toast(t("users.user_created"));
    await load();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function openGlobalManagement(user: User) {
  void loadGlobalManagement(user);
}

async function loadGlobalManagement(user: User) {
  try {
    const [settings, contributionData] = await Promise.all([
      api("GET", `/api/users/${encodeURIComponent(user.id)}/global-settings`),
      api(
        "GET",
        `/api/plugin-contributions/user-global/${encodeURIComponent(user.id)}`,
      ),
    ]);
    globalDraft.value = {
      userId: user.id,
      settings: normalizeGlobalSettings(settings),
      contributions: Array.isArray(contributionData)
        ? clone(contributionData)
        : [],
    };
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function closeGlobalManagement() {
  globalDraft.value = null;
}

function setGlobalSwitch(
  section: keyof GlobalSettings,
  key: string,
  value: boolean,
) {
  const draft = globalDraft.value;
  if (!draft) return;
  (draft.settings[section] as Record<string, unknown>)[key] = value;
}

function setGlobalInput(
  section: keyof GlobalSettings,
  key: string,
  value: string | number,
) {
  const draft = globalDraft.value;
  if (!draft) return;
  (draft.settings[section] as Record<string, unknown>)[key] = value;
}

function contributionValue(contribution: Contribution, key: string) {
  return contribution.values?.[key];
}
function setContributionValue(
  contribution: Contribution,
  key: string,
  value: unknown,
) {
  contribution.values ||= {};
  contribution.values[key] = value;
}
function inputValue(event: Event) {
  return (event.target as HTMLInputElement | HTMLTextAreaElement).value;
}
function contributionOptions(field: GlobalField): NxpOption[] {
  return (field.options || []).map((option) =>
    typeof option === "string"
      ? { value: option, label: option }
      : {
          value: String(option.value || ""),
          label: String(option.label || option.value || ""),
        },
  );
}

async function saveGlobalManagement() {
  const draft = globalDraft.value;
  if (!draft) return;
  try {
    await api(
      "PUT",
      `/api/users/${encodeURIComponent(draft.userId)}/global-settings`,
      draft.settings,
    );
    for (const contribution of draft.contributions) {
      await api(
        "PUT",
        `/api/plugin-contributions/user-global/${encodeURIComponent(draft.userId)}/${encodeURIComponent(contribution.pluginName || "")}/${encodeURIComponent(contribution.id || "")}`,
        { values: contribution.values || {} },
      );
    }
    closeGlobalManagement();
    toast(t("users.global.saved"));
    await load();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function openUserManagement(user: User) {
  userDraft.value = clone({ ...user, bindings: user.bindings || [] });
  expandedBindingId.value = null;
  bindingEditMode.value = false;
  addBindingOpen.value = false;
  selectedBindingIds.value = [];
}

function closeUserManagement() {
  userDraft.value = null;
  expandedBindingId.value = null;
  addBindingOpen.value = false;
  selectedBindingIds.value = [];
}

function bindingName(binding: Binding) {
  return (
    binding.scriptName ||
    scripts.value.find((script) => script.id === binding.scriptInstanceId)
      ?.name ||
    t("users.script_instance_not_found")
  );
}

function bindingValue(binding: Binding, field: keyof BindingEffective) {
  const category = ["enabled", "runDays", "maxSuccessfulRunsPerDay"].includes(
    field,
  )
    ? "general"
    : ["notifyEnabled", "smtpTo"].includes(field)
      ? "notification"
      : "advanced";
  if (
    binding.locks?.[category as keyof BindingLocks] &&
    binding.effective &&
    field in binding.effective
  )
    return binding.effective[field];
  return binding[field as keyof Binding] as unknown;
}

function setBindingValue(
  binding: Binding,
  field: keyof Binding,
  value: unknown,
) {
  const category = ["enabled", "runDays", "maxSuccessfulRunsPerDay"].includes(
    field,
  )
    ? "general"
    : ["notifyEnabled", "smtpTo"].includes(field)
      ? "notification"
      : "advanced";
  if (binding.locks?.[category as keyof BindingLocks]) return;
  (binding as unknown as Record<string, unknown>)[field] = value;
}

function toggleBinding(binding: Binding) {
  if (bindingEditMode.value) return;
  expandedBindingId.value =
    expandedBindingId.value === binding.scriptInstanceId
      ? null
      : binding.scriptInstanceId;
  addBindingOpen.value = false;
}

function toggleAddBindings() {
  if (bindingEditMode.value) return;
  addBindingOpen.value = !addBindingOpen.value;
  expandedBindingId.value = null;
  selectedBindingIds.value = [];
}

function toggleSelectedBinding(id: string) {
  selectedBindingIds.value = selectedBindingIds.value.includes(id)
    ? selectedBindingIds.value.filter((item) => item !== id)
    : [...selectedBindingIds.value, id];
}

async function addBindings() {
  const draft = userDraft.value;
  if (!draft || !selectedBindingIds.value.length) {
    toast(t("users.binding.choose_one"), "error");
    return;
  }
  try {
    for (const scriptId of selectedBindingIds.value) {
      await api("POST", `/api/users/${encodeURIComponent(draft.id)}/bindings`, {
        scriptInstanceId: scriptId,
        enabled: true,
        notifyEnabled: true,
        preRunScript: "",
        preRunOnceOnly: false,
        postRunScript: "",
        postRunOnFinalOnly: false,
        smtpTo: "",
        runDays: -1,
        maxSuccessfulRunsPerDay: -1,
      });
    }
    const refreshed = (await api(
      "GET",
      `/api/users/${encodeURIComponent(draft.id)}`,
    )) as User;
    userDraft.value = clone(refreshed);
    addBindingOpen.value = false;
    selectedBindingIds.value = [];
    toast(t("users.script_binding_added"));
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

async function removeBinding(binding: Binding) {
  const draft = userDraft.value;
  if (!draft) return;
  try {
    await api(
      "DELETE",
      `/api/users/${encodeURIComponent(draft.id)}/bindings/${encodeURIComponent(binding.scriptInstanceId)}`,
    );
    const refreshed = (await api(
      "GET",
      `/api/users/${encodeURIComponent(draft.id)}`,
    )) as User;
    userDraft.value = clone(refreshed);
    expandedBindingId.value = null;
    toast(t("users.script_binding_removed"));
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

async function saveUserManagement() {
  const draft = userDraft.value;
  if (!draft) return;
  const name = draft.name.trim();
  if (!name) {
    toast(t("users.validation.username_required"), "error");
    return;
  }
  try {
    await api("PUT", `/api/users/${encodeURIComponent(draft.id)}`, {
      name,
      remark: String(draft.remark || "").trim(),
    });
    for (const binding of draft.bindings || []) {
      await api(
        "PUT",
        `/api/users/${encodeURIComponent(draft.id)}/bindings/${encodeURIComponent(binding.scriptInstanceId)}`,
        {
          scriptInstanceId: binding.scriptInstanceId,
          enabled: binding.enabled !== false,
          notifyEnabled: binding.notifyEnabled !== false,
          smtpTo: binding.smtpTo || "",
          preRunScript: binding.preRunScript || "",
          preRunOnceOnly: binding.preRunOnceOnly === true,
          postRunScript: binding.postRunScript || "",
          postRunOnFinalOnly: binding.postRunOnFinalOnly === true,
          runDays: typeof binding.runDays === "number" ? binding.runDays : -1,
          maxSuccessfulRunsPerDay:
            typeof binding.maxSuccessfulRunsPerDay === "number"
              ? binding.maxSuccessfulRunsPerDay
              : -1,
          configInputs: binding.configInputs || {},
        },
      );
    }
    closeUserManagement();
    toast(t("users.user_settings_saved"));
    await load();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function askDelete(user: User) {
  deleteTarget.value = user;
  deleteName.value = "";
}
function closeDelete() {
  deleteTarget.value = null;
  deleteName.value = "";
}
async function confirmDelete() {
  const user = deleteTarget.value;
  if (!user || deleteName.value !== user.name) {
    toast(t("users.confirm.username_help"), "error");
    return;
  }
  try {
    await api("DELETE", `/api/users/${encodeURIComponent(user.id)}`, {
      confirmName: deleteName.value,
    });
    closeDelete();
    toast(t("users.deleted_user_value", { name: user.name }));
    await load();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

async function paintListSlots() {
  await nextTick();
  const slots =
    root.value?.querySelectorAll<HTMLElement>(
      '[data-plugin-slot="users.list.badges"]',
    ) || [];
  for (const slot of slots)
    await renderPluginSlot(slot, "users.list.badges", {
      mode: "list",
      primaryId: slot.dataset.pluginPrimaryId || "",
    });
}

onMounted(() => {
  disposed = false;
  setTopbarTitle(t("users.user_management"));
  countdownTimer = setInterval(refreshCountdowns, 1000);
  void load();
});

onBeforeUnmount(() => {
  disposed = true;
  if (countdownTimer) clearInterval(countdownTimer);
  const slots =
    root.value?.querySelectorAll<HTMLElement>("[data-plugin-slot]") || [];
  for (const slot of slots) void disposePluginSlot(slot);
});
</script>

<template>
  <main
    id="view"
    ref="root"
    class="view-root users-page"
    data-testid="main-view"
  >
    <NxpEmptyState v-if="loading" :title="t('common.loading')" />
    <NxpEmptyState
      v-else-if="error"
      :title="t('users.error.load')"
      :description="error"
      tone="danger"
    />
    <template v-else>
      <header class="page-head">
        <div class="page-head-copy">
          <div class="eyebrow">{{ t("users.account_management") }}</div>
          <h2>{{ t("users.user_management") }}</h2>
          <p class="page-kicker">{{ t("users.page.help") }}</p>
        </div>
        <div class="page-head-actions">
          <NxpButton
            class="primary"
            data-testid="open-global-user-modal"
            @click="openNewUser"
            >{{ t("users.add_user") }}</NxpButton
          >
        </div>
      </header>
      <NxpEmptyState
        v-if="!sortedUsers.length"
        :title="t('users.no_users_yet')"
        :description="t('users.page.empty_help')"
        ><button class="back-link" type="button" @click="openNewUser">
          {{ t("users.add_user") }}
        </button></NxpEmptyState
      >
      <section v-else class="card list-surface">
        <div class="script-grid global-user-list">
          <article
            v-for="user in sortedUsers"
            :key="user.id"
            class="script-card global-user-card"
            data-testid="global-user-card"
          >
            <span
              class="drag-handle"
              role="button"
              tabindex="0"
              :aria-label="t('users.global.order_help')"
              :title="t('common.drag_to_reorder')"
              ><NxpIcon name="grip"
            /></span>
            <span class="global-user-avatar-button" aria-hidden="true"
              ><img
                v-if="user.avatarUrl"
                class="global-user-avatar"
                :src="user.avatarUrl"
                alt=""
                loading="lazy"
              /><span
                v-else
                class="global-user-avatar global-user-avatar-fallback"
                >{{ initials(user.name) }}</span
              ><span class="global-user-avatar-mark" aria-hidden="true">+</span></span
            >
            <div class="script-main global-user-main">
              <div class="script-name-row">
                <strong class="global-user-name">{{ user.name }}</strong>
              </div>
              <div class="meta-line global-user-meta">
                <NxpBadge tone="muted">{{
                  t("users.binding.scripts_count", {
                    count: user.bindingCount ?? (user.bindings || []).length,
                  })
                }}</NxpBadge
                ><NxpBadge
                  v-for="badge in badgesByUser.get(user.id) || []"
                  :key="`${badge.pluginName}-${badge.id}`"
                  :tone="
                    badge.tone === 'ok' ||
                    badge.tone === 'warn' ||
                    badge.tone === 'bad' ||
                    badge.tone === 'blue'
                      ? badge.tone
                      : 'muted'
                  "
                  data-testid="plugin-user-badge"
                  :title="badge.title"
                  >{{ badge.label }}</NxpBadge
                ><span
                  class="plugin-slot user-plugin-slot"
                  data-plugin-slot="users.list.badges"
                  data-plugin-anchor="users.list.badges"
                  data-plugin-mode="list"
                  :data-plugin-primary-id="user.id"
                  hidden
                ></span
                ><NxpBadge tone="blue" class="global-user-next-run">{{
                  countdownByUser[user.id] ||
                  remainingLabel(user.nextRunAt || "")
                }}</NxpBadge>
              </div>
            </div>
            <div class="global-user-actions row-actions entity-actions">
              <button
                class="tertiary"
                type="button"
                @click.stop="openUserManagement(user)"
              >
                {{ t("users.user_management_button") }}</button
              ><button
                class="tertiary"
                type="button"
                @click.stop="openGlobalManagement(user)"
              >
                {{ t("users.global.open_action") }}</button
              ><button
                class="danger"
                type="button"
                @click.stop="askDelete(user)"
              >
                {{ t("users.delete_user") }}
              </button>
            </div>
          </article>
        </div>
      </section>
    </template>

    <div v-if="newUserOpen" class="modal-mask" role="presentation">
      <section
        class="modal secondary-surface"
        role="dialog"
        aria-modal="true"
        :aria-label="t('users.add_user')"
      >
        <div class="modal-header">
          <div><h3 class="modal-title">{{ t("users.add_user") }}</h3></div>
          <button
            class="icon-button modal-close"
            type="button"
            :aria-label="t('common.close')"
            @click.stop="closeNewUser"
          >
            <NxpIcon name="close" />
          </button>
        </div>
        <div class="modal-body">
          <div class="field">
            <label class="field-label" for="gu-name">{{
              t("users.user_name")
            }}</label
            ><input id="gu-name" v-model="newUserName" type="text" />
          </div>
          <div class="field">
            <label class="field-label" for="gu-remark">{{
              t("users.remark")
            }}</label
            ><textarea
              id="gu-remark"
              v-model="newUserRemark"
              rows="3"
            ></textarea>
          </div>
        </div>
        <div class="modal-footer">
          <button class="ghost" type="button" @click.stop="closeNewUser">
            {{ t("common.cancel") }}</button
          ><button
            class="primary"
            type="button"
            data-testid="save-global-user"
            @click.stop="createUser"
          >
            {{ t("common.save") }}
          </button>
        </div>
      </section>
    </div>

    <div v-if="globalDraft" class="modal-mask" role="presentation">
      <section
        class="modal secondary-surface"
        role="dialog"
        aria-modal="true"
        :aria-label="t('users.global.title')"
      >
        <div class="modal-header">
          <div><h3 class="modal-title">{{ t("users.global.title") }}</h3></div>
          <button
            class="icon-button modal-close"
            type="button"
            :aria-label="t('common.close')"
            @click.stop="closeGlobalManagement"
          >
            <NxpIcon name="close" />
          </button>
        </div>
        <div class="modal-body">
          <div class="global-management-grid">
            <section class="global-management-card">
              <div class="section-heading">
                <div>
                  <h3>{{ t("common.general") }}</h3>
                  <p class="muted">{{ t("users.global.general.help") }}</p>
                </div>
              </div>
              <div class="settings-list">
                <div class="switch-row">
                  <span class="field-label">{{
                    t("users.sync_general_settings")
                  }}</span
                  ><NxpSwitch
                    :model-value="globalDraft.settings.general.syncEnabled"
                    :aria-label="t('users.sync_general_settings')"
                    @update:model-value="
                      setGlobalSwitch('general', 'syncEnabled', $event)
                    "
                  />
                </div>
                <div class="switch-row">
                  <span class="field-label">{{ t("common.enabled") }}</span
                  ><NxpSwitch
                    :model-value="globalDraft.settings.general.enabled"
                    :aria-label="t('common.enabled')"
                    @update:model-value="
                      setGlobalSwitch('general', 'enabled', $event)
                    "
                  />
                </div>
              </div>
              <div class="field">
                <label class="field-label" for="gm-general-run-days">{{
                  t("common.run_days")
                }}</label
                ><input
                  id="gm-general-run-days"
                  :value="globalDraft.settings.general.runDays"
                  type="number"
                  min="-1"
                  @input="
                    setGlobalInput(
                      'general',
                      'runDays',
                      Number(inputValue($event)) || -1,
                    )
                  "
                />
              </div>
              <div class="field">
                <label class="field-label" for="gm-general-max-success">{{
                  t("users.maximum_successful_runs")
                }}</label
                ><input
                  id="gm-general-max-success"
                  :value="globalDraft.settings.general.maxSuccessfulRunsPerDay"
                  type="number"
                  min="-1"
                  @input="
                    setGlobalInput(
                      'general',
                      'maxSuccessfulRunsPerDay',
                      Number(inputValue($event)) || -1,
                    )
                  "
                />
              </div>
            </section>
            <section class="global-management-card">
              <div class="section-heading">
                <div>
                  <h3>{{ t("common.notifications") }}</h3>
                  <p class="muted">{{ t("users.global.notification_help") }}</p>
                </div>
              </div>
              <div class="settings-list">
                <div class="switch-row">
                  <span class="field-label">{{
                    t("users.binding.sync_notifications")
                  }}</span
                  ><NxpSwitch
                    :model-value="globalDraft.settings.notification.syncEnabled"
                    :aria-label="t('users.binding.sync_notifications')"
                    @update:model-value="
                      setGlobalSwitch('notification', 'syncEnabled', $event)
                    "
                  />
                </div>
                <div class="switch-row">
                  <span class="field-label">{{
                    t("users.enable_notifications")
                  }}</span
                  ><NxpSwitch
                    :model-value="
                      globalDraft.settings.notification.notifyEnabled
                    "
                    :aria-label="t('users.enable_notifications')"
                    @update:model-value="
                      setGlobalSwitch('notification', 'notifyEnabled', $event)
                    "
                  />
                </div>
              </div>
              <div class="field">
                <label class="field-label" for="gm-notification-smtp">{{
                  t("users.smtp_recipients")
                }}</label
                ><input
                  id="gm-notification-smtp"
                  :value="globalDraft.settings.notification.smtpTo"
                  type="text"
                  @input="
                    setGlobalInput('notification', 'smtpTo', inputValue($event))
                  "
                />
              </div>
            </section>
            <section class="global-management-card global-management-card-wide">
              <div class="section-heading">
                <div>
                  <h3>{{ t("users.advanced") }}</h3>
                  <p class="muted">{{ t("users.global.lifecycle_help") }}</p>
                </div>
              </div>
              <div class="settings-list">
                <div class="switch-row">
                  <span class="field-label">{{
                    t("users.sync_advanced_settings")
                  }}</span
                  ><NxpSwitch
                    :model-value="globalDraft.settings.advanced.syncEnabled"
                    :aria-label="t('users.sync_advanced_settings')"
                    @update:model-value="
                      setGlobalSwitch('advanced', 'syncEnabled', $event)
                    "
                  />
                </div>
              </div>
              <div class="field">
                <label class="field-label" for="gm-advanced-pre">{{
                  t("users.before_task_script_path")
                }}</label
                ><input
                  id="gm-advanced-pre"
                  :value="globalDraft.settings.advanced.preRunScript"
                  type="text"
                  @input="
                    setGlobalInput(
                      'advanced',
                      'preRunScript',
                      inputValue($event),
                    )
                  "
                />
              </div>
              <div class="field">
                <label class="field-label" for="gm-advanced-post">{{
                  t("users.after_task_script_path")
                }}</label
                ><input
                  id="gm-advanced-post"
                  :value="globalDraft.settings.advanced.postRunScript"
                  type="text"
                  @input="
                    setGlobalInput(
                      'advanced',
                      'postRunScript',
                      inputValue($event),
                    )
                  "
                />
              </div>
            </section>
          </div>
          <section
            v-if="globalDraft.contributions.length"
            class="global-management-plugins"
          >
            <div class="section-heading">
              <div>
                <h3>{{ t("common.plugin_settings") }}</h3>
                <p class="muted">{{ t("users.global.plugin_help") }}</p>
              </div>
            </div>
            <article
              v-for="contribution in globalDraft.contributions"
              :key="`${contribution.pluginName}-${contribution.id}`"
              class="global-management-plugin"
            >
              <div class="section-heading">
                <div>
                  <h4>
                    {{
                      contribution.pluginDisplayName || contribution.pluginName
                    }}
                  </h4>
                  <strong
                    v-if="
                      contribution.title &&
                      contribution.title !==
                        (contribution.pluginDisplayName ||
                          contribution.pluginName)
                    "
                    >{{ contribution.title }}</strong
                  >
                  <p v-if="contribution.description" class="muted">
                    {{ contribution.description }}
                  </p>
                </div>
              </div>
              <div class="plugin-contribution-fields">
                <template
                  v-for="field in contribution.fields || []"
                  :key="field.key"
                  ><div
                    v-if="
                      String(field.type || 'text').toLowerCase() === 'switch'
                    "
                    class="switch-row plugin-field"
                  >
                    <span class="field-label">{{ field.label }}</span
                    ><NxpSwitch
                      :model-value="
                        contributionValue(contribution, field.key) === true
                      "
                      semantic-role="button"
                      :aria-label="field.label"
                      :disabled="field.readOnly"
                      @update:model-value="
                        setContributionValue(contribution, field.key, $event)
                      "
                    />
                  </div>
                  <div
                    v-else-if="
                      String(field.type || 'text').toLowerCase() === 'textarea'
                    "
                    class="field plugin-field"
                  >
                    <label
                      class="field-label"
                      :for="`gm-plugin-${field.key}`"
                      >{{ field.label }}</label
                    ><textarea
                      :id="`gm-plugin-${field.key}`"
                      :value="
                        String(contributionValue(contribution, field.key) || '')
                      "
                      :readonly="field.readOnly"
                      @input="
                        setContributionValue(
                          contribution,
                          field.key,
                          inputValue($event),
                        )
                      "
                    ></textarea
                    ><span v-if="field.description" class="muted">{{
                      field.description
                    }}</span>
                  </div>
                  <div
                    v-else-if="
                      String(field.type || 'text').toLowerCase() === 'select'
                    "
                    class="field plugin-field"
                  >
                    <label class="field-label">{{ field.label }}</label
                    ><NxpSelect
                      :model-value="
                        String(contributionValue(contribution, field.key) || '')
                      "
                      :options="contributionOptions(field)"
                      :disabled="field.readOnly"
                      :aria-label="field.label"
                      @update:model-value="
                        setContributionValue(contribution, field.key, $event)
                      "
                    />
                  </div>
                  <div
                    v-else-if="
                      String(field.type || 'text').toLowerCase() === 'status'
                    "
                    class="field plugin-field"
                  >
                    <span class="field-label">{{ field.label }}</span
                    ><span class="plugin-status-value">{{
                      String(
                        contributionValue(contribution, field.key) ||
                          t("users.no_status"),
                      )
                    }}</span>
                  </div>
                  <div v-else class="field plugin-field">
                    <label
                      class="field-label"
                      :for="`gm-plugin-${field.key}`"
                      >{{ field.label }}</label
                    ><input
                      :id="`gm-plugin-${field.key}`"
                      :type="
                        String(field.type || 'text').toLowerCase() === 'secret'
                          ? 'password'
                          : 'text'
                      "
                      :value="
                        String(contributionValue(contribution, field.key) || '')
                      "
                      :readonly="field.readOnly"
                      @input="
                        setContributionValue(
                          contribution,
                          field.key,
                          inputValue($event),
                        )
                      "
                    /><span v-if="field.description" class="muted">{{
                      field.description
                    }}</span>
                  </div></template
                >
              </div>
            </article>
          </section>
        </div>
        <div class="modal-footer">
          <button
            class="primary"
            type="button"
            @click.stop="saveGlobalManagement"
          >
            {{ t("common.save") }}</button
          ><button
            class="ghost"
            type="button"
            @click.stop="closeGlobalManagement"
          >
            {{ t("common.cancel") }}
          </button>
        </div>
      </section>
    </div>

    <div v-if="userDraft" class="modal-mask" role="presentation">
      <section
        class="modal wide secondary-surface"
        role="dialog"
        aria-modal="true"
        :aria-label="t('users.user_management')"
      >
        <div class="modal-header">
          <div><h3 class="modal-title">{{ t("users.user_management") }}</h3></div>
          <button
            class="icon-button modal-close"
            type="button"
            :aria-label="t('common.close')"
            @click.stop="closeUserManagement"
          >
            <NxpIcon name="close" />
          </button>
        </div>
        <div class="modal-body">
          <section class="user-management-settings">
            <div class="field">
              <label class="field-label" for="um-name"
                >{{ t("users.user_name") }} <span class="req">*</span></label
              ><input id="um-name" v-model="userDraft.name" type="text" />
            </div>
            <div class="field">
              <label class="field-label" for="um-remark">{{
                t("users.remark")
              }}</label
              ><textarea
                id="um-remark"
                class="form-textarea"
                v-model="userDraft.remark"
                rows="3"
              ></textarea>
            </div>
          </section>
          <section class="subsection user-binding-section">
            <div class="section-heading um-binding-section-heading">
              <div>
                <h3>{{ t("users.bound_script_instances") }}</h3>
                <p class="muted">{{ t("users.binding.settings_help") }}</p>
              </div>
              <button
                class="ghost sm um-binding-edit-toggle"
                type="button"
                :aria-pressed="bindingEditMode"
                @click.stop="bindingEditMode = !bindingEditMode"
              >
                {{
                  bindingEditMode
                    ? t("users.done_editing")
                    : t("users.edit_bindings")
                }}
              </button>
            </div>
            <div
              class="um-add-area"
              :data-open="addBindingOpen ? '' : undefined"
            >
              <button
                class="um-add-script"
                type="button"
                data-testid="um-add-script"
                :aria-expanded="addBindingOpen"
                @click.stop="toggleAddBindings"
              >
                <NxpIcon name="plus" /> <span>{{ t("users.add_script") }}</span>
              </button>
              <div
                v-if="addBindingOpen"
                class="um-add-panel secondary-surface"
                data-testid="um-add-panel"
              >
                <div class="um-add-head">
                  <h4>{{ t("users.binding.choose") }}</h4>
                  <span class="muted">{{ t("users.multiple_selection") }}</span>
                </div>
                <div v-if="availableBindingScripts.length" class="um-add-grid">
                  <button
                    v-for="script in availableBindingScripts"
                    :key="script.id"
                    class="um-add-item"
                    type="button"
                    :aria-pressed="selectedBindingIds.includes(script.id)"
                    @click.stop="toggleSelectedBinding(script.id)"
                  >
                    <span class="um-add-item-copy"
                      ><strong>{{ script.name }}</strong></span
                    ><span aria-hidden="true">✓</span>
                  </button>
                </div>
                <NxpEmptyState
                  v-else
                  :title="t('users.no_script_instances_can_be_added')"
                  :description="t('users.binding.all_bound')"
                />
                <div class="um-add-actions">
                  <button
                    class="ghost"
                    type="button"
                    @click.stop="addBindingOpen = false"
                  >
                    {{ t("common.cancel") }}</button
                  ><button
                    class="primary"
                    type="button"
                    data-testid="um-add-confirm"
                    @click.stop="addBindings"
                  >
                    {{ t("common.confirm") }}
                  </button>
                </div>
              </div>
            </div>
            <div v-if="userDraft.bindings?.length" class="um-bindings">
              <article
                v-for="binding in userDraft.bindings"
                :key="binding.scriptInstanceId"
                class="um-binding-card"
                :class="{
                  'is-expanded': expandedBindingId === binding.scriptInstanceId,
                  'is-binding-editing': bindingEditMode,
                }"
                data-testid="um-binding-card"
                :data-binding-id="binding.scriptInstanceId"
              >
                <div class="um-binding-head">
                  <span
                    class="drag-handle um-binding-drag-handle"
                    role="button"
                    tabindex="0"
                    :aria-label="t('common.reorder.keyboard_help')"
                    :title="t('common.drag_to_reorder')"
                    ><NxpIcon name="grip" /></span
                  ><button
                    class="um-binding-toggle"
                    type="button"
                    data-action="toggle-um-binding"
                    :aria-expanded="
                      expandedBindingId === binding.scriptInstanceId
                    "
                    @click.stop="toggleBinding(binding)"
                  >
                    <span class="script-ico um-binding-ico" aria-hidden="true"
                      ><NxpIcon name="script" /></span><span class="um-binding-copy"
                      ><strong class="um-binding-name">{{
                        bindingName(binding)
                      }}</strong
                      ><span class="um-binding-badges"
                        ><NxpBadge
                          :tone="
                            bindingValue(binding, 'enabled') !== false
                              ? 'ok'
                              : 'muted'
                          "
                          >{{
                            bindingValue(binding, "enabled") !== false
                              ? t("users.enabled_badge")
                              : t("common.disabled")
                          }}</NxpBadge
                        ><NxpBadge tone="muted">{{
                          bindingValue(binding, "runDays") === 0
                            ? t("users.run_stopped")
                            : t("users.run_indefinitely")
                        }}</NxpBadge></span
                      ></span
                    ><span class="um-binding-bottom-arrow" aria-hidden="true"
                      ><NxpIcon name="chevronRight"
                    /></span></button
                  ><button
                    class="danger um-binding-remove"
                    type="button"
                    data-testid="um-remove-binding"
                    @click.stop="removeBinding(binding)"
                  >
                    {{ t("users.remove_binding") }}
                  </button>
                </div>
                <div
                  v-if="expandedBindingId === binding.scriptInstanceId"
                  class="um-binding-body"
                >
                  <section class="um-binding-option-section">
                    <div class="section-heading">
                      <div>
                        <h4>{{ t("common.general") }}</h4>
                        <p class="muted">
                          {{ t("users.binding.status_help") }}
                        </p>
                      </div>
                    </div>
                    <div class="switch-row">
                      <span class="field-label">{{ t("common.enabled") }}</span
                      ><NxpSwitch
                        :model-value="
                          bindingValue(binding, 'enabled') !== false
                        "
                        :aria-label="t('common.enabled')"
                        :disabled="binding.locks?.general === true"
                        @update:model-value="
                          setBindingValue(binding, 'enabled', $event)
                        "
                      />
                    </div>
                    <div class="field">
                      <label
                        class="field-label"
                        :for="`um-${binding.scriptInstanceId}-run-days`"
                        >{{ t("common.run_days") }}</label
                      ><input
                        :id="`um-${binding.scriptInstanceId}-run-days`"
                        :value="bindingValue(binding, 'runDays')"
                        type="number"
                        min="-1"
                        :disabled="binding.locks?.general === true"
                        @input="
                          setBindingValue(
                            binding,
                            'runDays',
                            Number(inputValue($event)) || -1,
                          )
                        "
                      />
                    </div>
                    <div class="field">
                      <label
                        class="field-label"
                        :for="`um-${binding.scriptInstanceId}-max-success`"
                        >{{ t("users.maximum_successful_runs") }}</label
                      ><input
                        :id="`um-${binding.scriptInstanceId}-max-success`"
                        :value="
                          bindingValue(binding, 'maxSuccessfulRunsPerDay')
                        "
                        type="number"
                        min="-1"
                        :disabled="binding.locks?.general === true"
                        @input="
                          setBindingValue(
                            binding,
                            'maxSuccessfulRunsPerDay',
                            Number(inputValue($event)) || -1,
                          )
                        "
                      />
                    </div>
                  </section>
                  <section class="um-binding-option-section">
                    <div class="section-heading">
                      <div>
                        <h4>{{ t("common.notifications") }}</h4>
                        <p class="muted">
                          {{ t("users.binding.result_notification_help") }}
                        </p>
                      </div>
                    </div>
                    <div class="switch-row">
                      <span class="field-label">{{
                        t("users.enable_notifications")
                      }}</span
                      ><NxpSwitch
                        :model-value="
                          bindingValue(binding, 'notifyEnabled') !== false
                        "
                        :aria-label="t('users.enable_notifications')"
                        :disabled="binding.locks?.notification === true"
                        @update:model-value="
                          setBindingValue(binding, 'notifyEnabled', $event)
                        "
                      />
                    </div>
                    <div class="field">
                      <label
                        class="field-label"
                        :for="`um-${binding.scriptInstanceId}-smtp`"
                        >{{ t("users.smtp_recipients") }}</label
                      ><input
                        :id="`um-${binding.scriptInstanceId}-smtp`"
                        :value="bindingValue(binding, 'smtpTo') || ''"
                        type="text"
                        :disabled="binding.locks?.notification === true"
                        @input="
                          setBindingValue(binding, 'smtpTo', inputValue($event))
                        "
                      />
                    </div>
                  </section>
                  <section class="um-binding-option-section">
                    <div class="section-heading">
                      <div>
                        <h4>{{ t("users.advanced") }}</h4>
                        <p class="muted">
                          {{ t("users.binding.lifecycle_help") }}
                        </p>
                      </div>
                    </div>
                    <div class="field">
                      <label class="field-label">{{
                        t("users.before_task_script_path")
                      }}</label
                      ><input
                        :value="bindingValue(binding, 'preRunScript') || ''"
                        type="text"
                        :disabled="binding.locks?.advanced === true"
                        @input="
                          setBindingValue(
                            binding,
                            'preRunScript',
                            inputValue($event),
                          )
                        "
                      />
                    </div>
                    <div class="field">
                      <label class="field-label">{{
                        t("users.after_task_script_path")
                      }}</label
                      ><input
                        :value="bindingValue(binding, 'postRunScript') || ''"
                        type="text"
                        :disabled="binding.locks?.advanced === true"
                        @input="
                          setBindingValue(
                            binding,
                            'postRunScript',
                            inputValue($event),
                          )
                        "
                      />
                    </div>
                  </section>
                </div>
              </article>
            </div>
            <NxpEmptyState
              v-else
              :title="t('users.no_script_instances_bound_yet')"
              :description="t('users.binding.add_help')"
            />
          </section>
        </div>
        <div class="modal-footer">
          <button
            class="primary"
            type="button"
            @click.stop="saveUserManagement"
          >
            {{ t("common.save") }}</button
          ><button
            class="ghost"
            type="button"
            data-action="close-modal"
            @click.stop="closeUserManagement"
          >
            {{ t("common.cancel") }}
          </button>
        </div>
      </section>
    </div>

    <div v-if="deleteTarget" class="modal-mask" role="presentation">
      <section
        class="modal secondary-surface"
        role="dialog"
        aria-modal="true"
        :aria-label="t('users.delete_user')"
      >
        <div class="modal-header">
          <div><h3 class="modal-title">{{ t("users.delete_user") }}</h3></div>
          <button
            class="icon-button modal-close"
            type="button"
            :aria-label="t('common.close')"
            @click.stop="closeDelete"
          >
            <NxpIcon name="close" />
          </button>
        </div>
        <div class="modal-body">
          <p class="modal-copy">
            {{ t("users.confirm.delete", { name: deleteTarget.name }) }}
          </p>
          <div class="field">
            <label class="field-label" for="gu-delete-name">{{
              t("users.confirm_username")
            }}</label
            ><input id="gu-delete-name" v-model="deleteName" type="text" />
          </div>
        </div>
        <div class="modal-footer">
          <button class="ghost" type="button" @click.stop="closeDelete">
            {{ t("common.cancel") }}</button
          ><button
            class="danger solid"
            type="button"
            data-testid="confirm-delete-global-user"
            @click.stop="confirmDelete"
          >
            {{ t("common.confirm_deletion") }}
          </button>
        </div>
      </section>
    </div>
  </main>
</template>

<style scoped>
.users-page .global-user-list {
  min-width: 0;
}
.users-page .global-user-avatar {
  flex: 0 0 auto;
}
.users-page .global-user-main {
  min-width: 0;
}
.users-page .global-user-next-run {
  white-space: nowrap;
}
.users-page .modal-body {
  max-height: min(72vh, 760px);
  overflow: auto;
}
.users-page .modal {
  width: min(960px, calc(100vw - 32px));
}
.users-page .plugin-contribution-fields {
  display: grid;
  gap: var(--space-3);
}
.users-page .plugin-field .muted {
  display: block;
  margin-top: var(--space-1);
}
.users-page .um-binding-card.is-expanded .um-binding-body {
  display: block;
}
.users-page .um-binding-body {
  display: grid;
  gap: var(--space-4);
  padding: var(--space-4);
}
.users-page .um-binding-toggle {
  min-width: 0;
  flex: 1;
}
.users-page .um-binding-copy {
  min-width: 0;
}
</style>
