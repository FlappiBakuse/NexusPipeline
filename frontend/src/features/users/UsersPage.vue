<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref } from "vue";
import { api, isAbortError } from "@legacy/core/api.js";
import { scriptPluginStatus, scriptPluginUnavailableMessage } from "@legacy/core/format.js";
import { state } from "@legacy/core/state.js";
import { renderPluginSlot } from "@legacy/core/plugin-slots.js";
import { disposePluginSlot } from "@legacy/core/plugin-runtime.js";
import { t } from "@legacy/core/i18n.js";
import { setTopbarTitle, toast } from "@legacy/core/ui.js";
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpEntityIcon from "../../ui/primitives/NxpEntityIcon.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";
import NxpModal from "../../ui/primitives/NxpModal.vue";
import NxpPathPicker from "../../ui/primitives/NxpPathPicker.vue";
import NxpNumberInput from "../../ui/primitives/NxpNumberInput.vue";
import NxpSelect, { type NxpOption } from "../../ui/primitives/NxpSelect.vue";
import NxpSwitchSetting from "../../ui/composites/NxpSwitchSetting.vue";
import { vSortable } from "../../ui/sortable";
import GlobalUserCard from "./GlobalUserCard.vue";

interface BindingEffective {
  enabled?: boolean;
  notifyEnabled?: boolean;
  smtpTo?: string;
  preRunScript?: string;
  postRunScript?: string;
  runDays?: number;
  maxSuccessfulRunsPerDay?: number;
  preRunOnceOnly?: boolean;
  postRunOnFinalOnly?: boolean;
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
  pluginType?: string;
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
  index?: number;
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
  pattern?: string;
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
const globalDraft = ref<GlobalDraft | null>(null);
const userDraft = ref<User | null>(null);
const expandedBindingId = ref<string | null>(null);
const bindingEditMode = ref(false);
const addBindingOpen = ref(false);
const selectedBindingIds = ref<string[]>([]);
const deleteTarget = ref<User | null>(null);
const deleteName = ref("");
const root = ref<HTMLElement | null>(null);
const globalSlotRoot = ref<HTMLElement | null>(null);
const secretActions = reactive<Record<string, "keep" | "set" | "clear">>({});
const configEdit = ref<{ userId: string; scriptId: string; userName: string; scriptName: string; mode: string } | null>(null);
const configChooser = ref<{ userId: string; scriptId: string; userName: string; scriptName: string; freshAvailable: boolean } | null>(null);
const configCandidates = ref<{ userId: string; scriptId: string; userName: string; scriptName: string; mode: string; inputName: string; candidates: string[] } | null>(null);
let disposed = false;
let countdownTimer: ReturnType<typeof setInterval> | null = null;
const bindingMotionDuration = 180;

const sortedUsers = computed(() =>
  users.value.slice().sort((a, b) => (a.index ?? 0) - (b.index ?? 0)),
);
const availableBindingScripts = computed(() => {
  const bound = new Set(
    (userDraft.value?.bindings || []).map(
      (binding) => binding.scriptInstanceId,
    ),
  );
  return scripts.value
    .slice()
    .sort((a, b) => (a.index ?? 0) - (b.index ?? 0))
    .filter((script) => {
      if (bound.has(script.id)) return false;
      const status = scriptPluginStatus(script, plugins.value);
      return !status.specialized || status.available;
    });
});

const clone = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
const errorText = (reason: unknown) =>
  reason instanceof Error ? reason.message : String(reason);

const fieldKey = (contribution: Contribution, field: GlobalField) =>
  `${contribution.pluginName || "plugin"}::${contribution.id || "settings"}::${field.key}`;
const fieldId = (contribution: Contribution, field: GlobalField) =>
  `gm-plugin-${fieldKey(contribution, field).replace(/[^a-zA-Z0-9_-]/g, "-")}`;
const fieldType = (field: GlobalField) => String(field.type || "text").toLowerCase();
const PRE_ONLY_MARKER = "%FIRST%";
const POST_FINAL_MARKER = "%LAST%";

function encodePrePost(marker: string, onceOnly: unknown, value: unknown) {
  return `${onceOnly === true ? `${marker} ` : ""}${String(value || "")}`;
}

function splitPrePost(marker: string, value: unknown) {
  const text = String(value || "").trim();
  return {
    onceOnly: text.startsWith(marker),
    value: text.replace(new RegExp(`^${marker}\\s*`), ""),
  };
}

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
async function reorderUsers(ids: string[]) {
  const byId = new Map(users.value.map(user => [user.id, user]));
  const next = ids.map(id => byId.get(id)).filter((user): user is User => Boolean(user));
  if (next.length !== users.value.length || next.every((user, index) => user.id === sortedUsers.value[index]?.id)) return;
  users.value = next.map((item, index) => ({ ...item, index }));
  try {
    await api("PUT", "/api/users/order", { ids: next.map(item => item.id) });
    toast(t("users.user_order_saved"));
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
    await load();
  }
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
  if (new TextEncoder().encode(name).length > 64) {
    toast(t("users.validation.username_length", { bytes: 64 }), "error");
    return;
  }
  if (users.value.some(user => user.name.toLocaleLowerCase() === name.toLocaleLowerCase())) {
    toast(t("users.validation.username_duplicate"), "error");
    return;
  }
  try {
    await api("POST", "/api/users", { name });
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
    secretActionsReset();
    globalDraft.value = {
      userId: user.id,
      settings: normalizeGlobalSettings(settings),
      contributions: Array.isArray(contributionData)
        ? clone(contributionData)
        : [],
    };
    await nextTick();
    if (globalSlotRoot.value) {
      await renderPluginSlot(globalSlotRoot.value, "users.global.sections", {
        mode: "user",
        primaryId: user.id,
      });
    }
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function closeGlobalManagement() {
  if (globalSlotRoot.value) void disposePluginSlot(globalSlotRoot.value);
  globalDraft.value = null;
  secretActionsReset();
}

function secretActionsReset() {
  Object.keys(secretActions).forEach(key => delete secretActions[key]);
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

function setGlobalPath(key: "preRunScript" | "postRunScript", value: string) {
  const parsed = splitPrePost(key === "preRunScript" ? PRE_ONLY_MARKER : POST_FINAL_MARKER, value);
  setGlobalInput("advanced", key, parsed.value);
  setGlobalSwitch("advanced", key === "preRunScript" ? "preRunOnceOnly" : "postRunOnFinalOnly", parsed.onceOnly);
}

async function browseGlobalPath(key: "preRunScript" | "postRunScript", kind: "file" | "folder") {
  const draft = globalDraft.value;
  if (!draft) return;
  try {
    const result = await api("POST", "/api/native-dialog", {
      kind,
      title: key === "preRunScript" ? t("users.before_task_script_path") : t("users.after_task_script_path"),
      initialPath: String(draft.settings.advanced[key] || "") || undefined,
      filter: t("users.script_file_filter"),
    }) as { path?: string };
    if (result?.path) setGlobalPath(key, result.path);
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
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

function contributionStringValue(contribution: Contribution, field: GlobalField) {
  const value = contributionValue(contribution, field.key);
  return fieldType(field) === "secret" && value && typeof value === "object"
    ? ""
    : String(value ?? "");
}

function secretIsConfigured(contribution: Contribution, field: GlobalField) {
  const value = contributionValue(contribution, field.key);
  return Boolean(value && typeof value === "object" && (value as { configured?: boolean }).configured === true);
}

function setSecretValue(contribution: Contribution, field: GlobalField, value: string) {
  const key = fieldKey(contribution, field);
  secretActions[key] = value ? "set" : (secretIsConfigured(contribution, field) ? "keep" : "set");
  setContributionValue(contribution, field.key, value);
}

function clearSecret(contribution: Contribution, field: GlobalField) {
  secretActions[fieldKey(contribution, field)] = "clear";
  setContributionValue(contribution, field.key, "");
}

function contributionValuesForSave(contribution: Contribution) {
  const values: Record<string, unknown> = {};
  for (const field of contribution.fields || []) {
    const type = fieldType(field);
    const value = contributionValue(contribution, field.key);
    if (type === "secret") {
      const action = secretActions[fieldKey(contribution, field)] || (secretIsConfigured(contribution, field) ? "keep" : "set");
      values[field.key] = action === "set" ? { action, value: String(value || "") } : { action };
    } else if (type === "multi-select") {
      values[field.key] = Array.isArray(value) ? value.map(String) : [];
    } else {
      values[field.key] = value;
    }
  }
  return values;
}

function validateContributionFields(contribution: Contribution) {
  for (const field of contribution.fields || []) {
    if (!field.required || field.readOnly || fieldType(field) === "status") continue;
    const value = contributionValue(contribution, field.key);
    if (fieldType(field) === "secret" && secretIsConfigured(contribution, field) && secretActions[fieldKey(contribution, field)] !== "clear") continue;
    const empty = fieldType(field) === "multi-select" ? !Array.isArray(value) || !value.length : !String(value ?? "").trim();
    if (empty) return false;
  }
  return true;
}

async function browseContributionPath(contribution: Contribution, field: GlobalField, kind: "file" | "folder") {
  try {
    const result = await api("POST", "/api/native-dialog", {
      kind,
      title: field.label,
      initialPath: contributionStringValue(contribution, field),
      filter: "",
    }) as { path?: string };
    if (result?.path) setContributionValue(contribution, field.key, result.path);
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
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

function contributionMultiValue(contribution: Contribution, field: GlobalField): string[] {
  const value = contributionValue(contribution, field.key);
  return Array.isArray(value) ? value.map(String) : [];
}

async function saveGlobalManagement() {
  const draft = globalDraft.value;
  if (!draft) return;
  try {
    if (draft.contributions.some(contribution => !validateContributionFields(contribution))) {
      toast(t("common.plugin.settings_required"), "error");
      return;
    }
    await api(
      "PUT",
      `/api/users/${encodeURIComponent(draft.userId)}/global-settings`,
      draft.settings,
    );
    for (const contribution of draft.contributions) {
      await api(
        "PUT",
        `/api/plugin-contributions/user-global/${encodeURIComponent(draft.userId)}/${encodeURIComponent(contribution.pluginName || "")}/${encodeURIComponent(contribution.id || "")}`,
        { values: contributionValuesForSave(contribution) },
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
  void paintBindingSlots();
}

function closeUserManagement() {
  const slots = root.value?.querySelectorAll<HTMLElement>('[data-plugin-slot="users.binding.sections"]') || [];
  for (const slot of slots) void disposePluginSlot(slot);
  userDraft.value = null;
  expandedBindingId.value = null;
  bindingEditMode.value = false;
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

function bindingStatus(binding: Binding) {
  const script = scripts.value.find(item => item.id === binding.scriptInstanceId);
  return script ? scriptPluginUnavailableMessage(script, plugins.value) : "";
}

function bindingPluginStatus(binding: Binding) {
  const script = scripts.value.find(item => item.id === binding.scriptInstanceId);
  return script ? scriptPluginStatus(script, plugins.value) : null;
}

function bindingEnabled(binding: Binding) {
  return bindingValue(binding, "enabled") !== false && bindingDays(binding) !== 0;
}

function numericValue(value: unknown, fallback = -1) {
  const number = Number(value);
  return Number.isFinite(number) ? number : fallback;
}

function maxRunDays() {
  const limits = (state as typeof state & { limits?: { maxRunDays?: number } }).limits;
  return Number(limits?.maxRunDays ?? 365);
}

function maxSuccessfulRunsPerDay() {
  const limits = (state as typeof state & { limits?: { maxSuccessfulRunsPerDay?: number } }).limits;
  return Number(limits?.maxSuccessfulRunsPerDay ?? 10);
}

function bindingOverrideHelp(binding: Binding, category: keyof BindingLocks) {
  return binding.locks?.[category]
    ? t("users.binding.global_override.help", {
        global: t("users.global.title"),
        script: t("common.script_instance"),
      })
    : "";
}

function bindingDays(binding: Binding) {
  const value = bindingValue(binding, "runDays");
  return typeof value === "number" ? value : -1;
}

function bindingDaysLabel(binding: Binding) {
  const days = bindingDays(binding);
  if (days === 0) return t("users.run_stopped");
  if (days > 0) return t("users.schedule.days_left", { days });
  return t("users.run_indefinitely");
}

function bindingDaysTone(binding: Binding): "muted" | "blue" | "warn" {
  const days = bindingDays(binding);
  return days === 0 ? "warn" : days > 0 ? "blue" : "muted";
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

function setBindingPath(binding: Binding, key: "preRunScript" | "postRunScript", value: string) {
  const parsed = splitPrePost(key === "preRunScript" ? PRE_ONLY_MARKER : POST_FINAL_MARKER, value);
  setBindingValue(binding, key, parsed.value);
  setBindingValue(binding, key === "preRunScript" ? "preRunOnceOnly" : "postRunOnFinalOnly", parsed.onceOnly);
}

async function browseBindingPath(binding: Binding, key: "preRunScript" | "postRunScript", kind: "file" | "folder") {
  if (!userDraft.value) return;
  try {
    const result = await api("POST", "/api/native-dialog", {
      kind,
      title: key === "preRunScript" ? t("users.before_task_script_path") : t("users.after_task_script_path"),
      initialPath: String(bindingValue(binding, key) || "") || undefined,
      filter: t("users.script_file_filter"),
    }) as { path?: string };
    if (result?.path) setBindingPath(binding, key, result.path);
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function captureBindingRects() {
  const cards = root.value?.querySelectorAll<HTMLElement>('[data-testid="um-binding-card"]') || [];
  return new Map(
    Array.from(cards)
      .map(card => [card.dataset.bindingId || "", card.getBoundingClientRect()] as const)
      .filter(([id]) => Boolean(id)),
  );
}

function playBindingLayoutTransition(previous: Map<string, DOMRect>) {
  if (typeof requestAnimationFrame !== "function") return;
  requestAnimationFrame(() => {
    const cards = root.value?.querySelectorAll<HTMLElement>('[data-testid="um-binding-card"]') || [];
    for (const card of cards) {
      const id = card.dataset.bindingId || "";
      const before = previous.get(id);
      if (!before) continue;
      const after = card.getBoundingClientRect();
      const dx = before.left - after.left;
      const dy = before.top - after.top;
      const scaleX = after.width ? before.width / after.width : 1;
      const scaleY = after.height ? before.height / after.height : 1;
      if (Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5 && Math.abs(scaleX - 1) < 0.005 && Math.abs(scaleY - 1) < 0.005) continue;
      card.animate(
        [
          { transform: `translate(${dx}px, ${dy}px) scale(${scaleX}, ${scaleY})` },
          { transform: "translate(0, 0) scale(1, 1)" },
        ],
        {
          duration: bindingMotionDuration,
          easing: "cubic-bezier(.2,.8,.2,1)",
          fill: "both",
        },
      );
    }
  });
}

async function toggleBinding(binding: Binding) {
  if (bindingEditMode.value) return;
  const unavailableMessage = bindingStatus(binding);
  if (unavailableMessage) {
    toast(unavailableMessage, "error");
    return;
  }
  const previous = captureBindingRects();
  expandedBindingId.value =
    expandedBindingId.value === binding.scriptInstanceId
      ? null
      : binding.scriptInstanceId;
  addBindingOpen.value = false;
  await nextTick();
  playBindingLayoutTransition(previous);
  void paintBindingSlots();
}

function toggleBindingEdit() {
  bindingEditMode.value = !bindingEditMode.value;
  if (bindingEditMode.value) {
    expandedBindingId.value = null;
    addBindingOpen.value = false;
    selectedBindingIds.value = [];
  }
  void paintBindingSlots();
}
function canReorderBindings() {
  return !bindingEditMode.value && !expandedBindingId.value;
}
async function reorderBindings(ids: string[]) {
  const draft = userDraft.value;
  if (!draft || !canReorderBindings()) return;
  const byId = new Map((draft.bindings || []).map(binding => [binding.scriptInstanceId, binding]));
  const next = ids.map(id => byId.get(id)).filter((binding): binding is NonNullable<User["bindings"]>[number] => Boolean(binding));
  if (next.length !== (draft.bindings || []).length || next.every((binding, index) => binding.scriptInstanceId === draft.bindings?.[index]?.scriptInstanceId)) return;
  draft.bindings = next;
  try {
    await api("PUT", `/api/users/${encodeURIComponent(draft.id)}/bindings/order`, { ids: next.map(item => item.scriptInstanceId) });
    toast(t("users.bound_script_order_saved"));
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
    await refreshUserDraft(draft.id);
  }
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
    await paintBindingSlots();
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

async function paintBindingSlots() {
  await nextTick();
  const slots = root.value?.querySelectorAll<HTMLElement>('[data-plugin-slot="users.binding.sections"]') || [];
  for (const slot of slots) {
    await renderPluginSlot(slot, "users.binding.sections", {
      mode: "binding",
      primaryId: slot.dataset.pluginPrimaryId || "",
      secondaryId: userDraft.value?.id || "",
    });
  }
}

async function refreshUserDraft(userId: string) {
  if (userDraft.value?.id !== userId) {
    await load();
    return;
  }
  try {
    userDraft.value = clone(await api("GET", `/api/users/${encodeURIComponent(userId)}`) as User);
    await load();
    await paintBindingSlots();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

async function uploadAvatar(userId: string) {
  const input = document.createElement("input");
  input.type = "file";
  input.accept = "image/png,image/jpeg,image/webp";
  input.addEventListener("change", () => {
    const file = input.files?.[0];
    if (!file) return;
    if (!["image/png", "image/jpeg", "image/webp"].includes(file.type)) {
      toast(t("users.validation.avatar_type"), "error");
      return;
    }
    if (file.size > 5 * 1024 * 1024) {
      toast(t("users.validation.avatar_size"), "error");
      return;
    }
    const reader = new FileReader();
    reader.onload = async () => {
      try {
        const dataUrl = String(reader.result || "");
        await api("POST", `/api/users/${encodeURIComponent(userId)}/avatar`, { mimeType: file.type, data: dataUrl.split(",", 2)[1] || "" });
        toast(t("users.avatar_updated"));
        await refreshUserDraft(userId);
      } catch (reason) {
        if (!isAbortError(reason)) toast(errorText(reason), "error");
      }
    };
    reader.readAsDataURL(file);
  }, { once: true });
  input.click();
}

async function removeAvatar(userId: string) {
  try {
    await api("DELETE", `/api/users/${encodeURIComponent(userId)}/avatar`);
    toast(t("users.avatar.default_restored"));
    await refreshUserDraft(userId);
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

async function openConfigEdit(binding: Binding) {
  const user = userDraft.value;
  if (!user) return;
  const unavailableMessage = bindingStatus(binding);
  if (unavailableMessage) {
    toast(unavailableMessage, "error");
    return;
  }
  try {
    const status = await api("GET", `/api/users/${encodeURIComponent(user.id)}/bindings/${encodeURIComponent(binding.scriptInstanceId)}/edit-config`) as { hasSnapshot?: boolean };
    const details = {
      userId: user.id,
      scriptId: binding.scriptInstanceId,
      userName: user.name,
      scriptName: bindingName(binding),
    };
    if (status?.hasSnapshot) {
      await beginConfigEdit({ ...details, mode: "normal" });
      return;
    };
    const script = scripts.value.find(item => item.id === binding.scriptInstanceId);
    const plugin = plugins.value.find(item => item.name === script?.pluginType) as (Plugin & { noFreshConfig?: boolean }) | undefined;
    configChooser.value = { ...details, freshAvailable: plugin?.noFreshConfig !== true };
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function configEditEndpoint(item: { userId: string; scriptId: string }) {
  return `/api/users/${encodeURIComponent(item.userId)}/bindings/${encodeURIComponent(item.scriptId)}/edit-config`;
}

function configEditErrorData(reason: unknown) {
  const value = reason as { code?: string; data?: { inputName?: string; candidates?: unknown[] } } | null;
  return value?.code === "config_input_mismatch" && Array.isArray(value.data?.candidates)
    ? { inputName: String(value.data?.inputName || ""), candidates: value.data.candidates.map(item => String(item || "")).filter(Boolean) }
    : null;
}

function createRequesterWindowToken() {
  const cryptoApi = globalThis.crypto;
  if (cryptoApi && typeof cryptoApi.getRandomValues === "function") {
    const bytes = new Uint8Array(8);
    cryptoApi.getRandomValues(bytes);
    return Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
  }
  return Math.random().toString(36).slice(2, 18).padEnd(16, "0");
}

function waitForRequesterTitlePaint() {
  return new Promise<void>(resolve => {
    if (typeof requestAnimationFrame === "function") {
      requestAnimationFrame(() => resolve());
      return;
    }
    setTimeout(resolve, 0);
  });
}

async function beginConfigEdit(item: { userId: string; scriptId: string; userName: string; scriptName: string; mode: string }, inputOverride?: { name: string; value: string }) {
  const requesterWindowToken = createRequesterWindowToken();
  const previousTitle = document.title;
  document.title = `${t("users.nexuspipeline_core")} · ${requesterWindowToken}`;
  try {
    const request: Record<string, unknown> = { action: "start", mode: item.mode };
    if (inputOverride) {
      request.configInputName = inputOverride.name;
      request.configInputValue = inputOverride.value;
    }
    request.requesterWindowToken = requesterWindowToken;
    await waitForRequesterTitlePaint();
    await api("POST", configEditEndpoint(item), request);
    configChooser.value = null;
    configCandidates.value = null;
    configEdit.value = item;
  } catch (reason) {
    const candidateData = configEditErrorData(reason);
    if (candidateData) {
      if (!candidateData.inputName) {
        toast(t("users.config.input_missing"), "error");
        return;
      }
      configChooser.value = null;
      configCandidates.value = { ...item, inputName: candidateData.inputName, candidates: candidateData.candidates };
      return;
    }
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  } finally {
    document.title = previousTitle;
  }
}

async function chooseConfigMode(mode: "fresh" | "reuse") {
  const chooser = configChooser.value;
  if (!chooser || (mode === "fresh" && !chooser.freshAvailable)) return;
  await beginConfigEdit({ ...chooser, mode });
}

async function chooseConfigCandidate(candidate: string) {
  const chooser = configCandidates.value;
  if (!chooser) return;
  configCandidates.value = null;
  await beginConfigEdit({ userId: chooser.userId, scriptId: chooser.scriptId, userName: chooser.userName, scriptName: chooser.scriptName, mode: chooser.mode }, { name: chooser.inputName, value: candidate });
}

async function finishConfigEdit(action: "done" | "cancel") {
  const edit = configEdit.value;
  if (!edit) return;
  try {
    const result = await api("POST", configEditEndpoint(edit), { action }) as { validation?: { toasts?: Array<{ message?: string; kind?: string }> } };
    configEdit.value = null;
    toast(action === "done"
      ? (edit.mode === "fresh" ? t("users.config.snapshot_saved") : t("users.user_configuration_saved", { user: edit.userName }))
      : (edit.mode === "reuse" ? t("common.cancelled") : t("users.config.cancelled_restored")));
    for (const item of result?.validation?.toasts || []) if (item.message) toast(item.message, item.kind || "info");
    await refreshUserDraft(edit.userId);
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function closeConfigEdit() {
  configEdit.value = null;
  configChooser.value = null;
  configCandidates.value = null;
}

async function saveUserManagement() {
  const draft = userDraft.value;
  if (!draft) return;
  const name = draft.name.trim();
  if (!name) {
    toast(t("users.validation.username_required"), "error");
    return;
  }
  if (new TextEncoder().encode(name).length > 64) {
    toast(t("users.validation.username_length", { bytes: 64 }), "error");
    return;
  }
  if (users.value.some(user => user.id !== draft.id && user.name.toLocaleLowerCase() === name.toLocaleLowerCase())) {
    toast(t("users.validation.username_duplicate"), "error");
    return;
  }
  const remark = String(draft.remark || "").trim();
  if (new TextEncoder().encode(remark).length > 512) {
    toast(t("users.validation.remark_length", { bytes: 512 }), "error");
    return;
  }
  try {
    await api("PUT", `/api/users/${encodeURIComponent(draft.id)}`, {
      name,
      remark,
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
      />
      <section v-else class="card list-surface">
        <TransitionGroup v-sortable="{ onDrop: reorderUsers }" name="nxp-card" tag="div" class="script-grid global-user-list">
          <GlobalUserCard
            v-for="user in sortedUsers"
            :key="user.id"
            :user="user"
            :badges="badgesByUser.get(user.id) || []"
            :next-label="countdownByUser[user.id] || remainingLabel(user.nextRunAt || '')"
            :initials="initials(user.name)"
            :translate="t"
            @upload="uploadAvatar"
            @manage="openUserManagement"
            @global-manage="openGlobalManagement"
            @remove="askDelete"
          />
        </TransitionGroup>
      </section>
    </template>

    <NxpModal
      :open="newUserOpen"
      :closeable="false"
      :locked="true"
      :aria-label="t('users.add_user')"
      panel-class="secondary-surface"
      data-locked
    >
      <template #header>
          <div><h3 class="modal-title">{{ t("users.add_user") }}</h3></div>
          <button
            class="icon-button modal-close"
            type="button"
            :aria-label="t('common.close')"
            @click.stop="closeNewUser"
          >
            <NxpIcon name="close" />
          </button>
      </template>
          <div class="field">
            <label class="field-label" for="gu-name">{{
              t("users.user_name")
            }} <span class="req">*</span></label
            ><input id="gu-name" v-model="newUserName" type="text" />
            <span class="muted">{{ t("users.username_case_insensitive") }}</span>
          </div>
      <template #footer>
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
      </template>
    </NxpModal>
    <NxpModal
      :open="Boolean(globalDraft)"
      :closeable="false"
      :locked="true"
      :aria-label="t('users.global.title')"
      panel-class="secondary-surface"
      size="wide"
      data-locked
    >
      <template #header>
          <div><h3 class="modal-title">{{ t("users.global.title") }}</h3></div>
          <button
            class="icon-button modal-close"
            type="button"
            :aria-label="t('common.close')"
            @click.stop="closeGlobalManagement"
          >
            <NxpIcon name="close" />
          </button>
      </template>
      <template v-if="globalDraft">
          <div class="global-management-grid">
            <section class="global-management-card">
              <div class="section-heading">
                <div>
                  <h3>{{ t("common.general") }}</h3>
                  <p class="muted">{{ t("users.global.general.help") }}</p>
                </div>
              </div>
              <div class="settings-list">
                <NxpSwitchSetting
                  :label="t('users.sync_general_settings')"
                  :description="t('users.global.general_override_help')"
                  :model-value="globalDraft.settings.general.syncEnabled"
                  :aria-label="t('users.sync_general_settings')"
                  @update:model-value="
                      setGlobalSwitch('general', 'syncEnabled', $event)
                    "
                />
                <NxpSwitchSetting
                  :label="t('common.enabled')"
                  :description="t('users.binding.run_days.zero_help')"
                  :model-value="globalDraft.settings.general.enabled"
                  :aria-label="t('common.enabled')"
                  @update:model-value="
                      setGlobalSwitch('general', 'enabled', $event)
                    "
                />
              </div>
              <div class="field">
                <label class="field-label" for="gm-general-run-days">{{
                  t("common.run_days")
                }}</label>
                <NxpNumberInput
                  id="gm-general-run-days"
                  :model-value="globalDraft.settings.general.runDays"
                  :min="-1"
                  :max="maxRunDays()"
                  :placeholder="t('users.global.run_days.placeholder')"
                  :help="t('users.global.run_days.help')"
                  :aria-label="t('common.run_days')"
                  @update:model-value="setGlobalInput('general', 'runDays', numericValue($event))"
                />
              </div>
              <div class="field">
                <label class="field-label" for="gm-general-max-success">{{
                  t("users.maximum_successful_runs")
                }}</label>
                <NxpNumberInput
                  id="gm-general-max-success"
                  :model-value="globalDraft.settings.general.maxSuccessfulRunsPerDay"
                  :min="-1"
                  :max="maxSuccessfulRunsPerDay()"
                  :placeholder="t('users.unlimited_placeholder')"
                  :help="t('users.binding.daily_limit.help')"
                  :aria-label="t('users.maximum_successful_runs')"
                  @update:model-value="setGlobalInput('general', 'maxSuccessfulRunsPerDay', numericValue($event))"
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
                <NxpSwitchSetting
                  :label="t('users.binding.sync_notifications')"
                  :description="t('users.global.notification_override_help')"
                  :model-value="globalDraft.settings.notification.syncEnabled"
                  :aria-label="t('users.binding.sync_notifications')"
                  @update:model-value="
                      setGlobalSwitch('notification', 'syncEnabled', $event)
                    "
                />
                <NxpSwitchSetting
                  :label="t('users.enable_notifications')"
                  :description="t('users.global.notification.enabled_help')"
                  :model-value="
                      globalDraft.settings.notification.notifyEnabled
                    "
                    :aria-label="t('users.enable_notifications')"
                    @update:model-value="
                      setGlobalSwitch('notification', 'notifyEnabled', $event)
                    "
                />
              </div>
              <div class="field" :data-help="t('users.global.notification.smtp_help')">
                <label class="field-label" for="gm-notification-smtp">{{
                  t("users.smtp_recipients")
                }}</label
                ><input
                  id="gm-notification-smtp"
                  :value="globalDraft.settings.notification.smtpTo"
                  type="text"
                  :placeholder="t('users.binding.smtp_inherit_help')"
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
                <NxpSwitchSetting
                  :label="t('users.sync_advanced_settings')"
                  :description="t('users.global.advanced_override_help')"
                  :model-value="globalDraft.settings.advanced.syncEnabled"
                  :aria-label="t('users.sync_advanced_settings')"
                  @update:model-value="
                      setGlobalSwitch('advanced', 'syncEnabled', $event)
                    "
                />
              </div>
              <div class="field">
                <label class="field-label" for="gm-advanced-pre">{{
                  t("users.before_task_script_path")
                }}</label
                ><NxpPathPicker
                  id="gm-advanced-pre"
                  :model-value="encodePrePost(PRE_ONLY_MARKER, globalDraft.settings.advanced.preRunOnceOnly, globalDraft.settings.advanced.preRunScript)"
                  kind="file"
                  :placeholder="t('users.pre_task_placeholder')"
                  :aria-label="t('users.before_task_script_path')"
                  :filter="t('users.script_file_filter')"
                  :help="t('users.pre_task_help')"
                  @update:model-value="setGlobalPath('preRunScript', $event)"
                  @browse="browseGlobalPath('preRunScript', $event)"
                />
              </div>
              <div class="field">
                <label class="field-label" for="gm-advanced-post">{{
                  t("users.after_task_script_path")
                }}</label
                ><NxpPathPicker
                  id="gm-advanced-post"
                  :model-value="encodePrePost(POST_FINAL_MARKER, globalDraft.settings.advanced.postRunOnFinalOnly, globalDraft.settings.advanced.postRunScript)"
                  kind="file"
                  :placeholder="t('users.post_task_placeholder')"
                  :aria-label="t('users.after_task_script_path')"
                  :filter="t('users.script_file_filter')"
                  :help="t('users.post_task_help')"
                  @update:model-value="setGlobalPath('postRunScript', $event)"
                  @browse="browseGlobalPath('postRunScript', $event)"
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
                <template v-for="field in contribution.fields || []" :key="field.key">
                  <NxpSwitchSetting
                    v-if="fieldType(field) === 'switch'"
                    class="plugin-field"
                    :label="`${field.label}${field.required ? ' *' : ''}`"
                    :description="field.description"
                    :model-value="contributionValue(contribution, field.key) === true"
                    :aria-label="field.label"
                    :disabled="field.readOnly"
                    @update:model-value="setContributionValue(contribution, field.key, $event)"
                  />
                  <div v-else-if="fieldType(field) === 'textarea'" class="field plugin-field" :data-help="field.description || undefined">
                    <label class="field-label" :for="fieldId(contribution, field)">{{ field.label }}<span v-if="field.required" class="req"> *</span></label>
                    <textarea :id="fieldId(contribution, field)" :value="contributionStringValue(contribution, field)" :maxlength="field.maxLength || undefined" :placeholder="field.placeholder || undefined" :readonly="field.readOnly" @input="setContributionValue(contribution, field.key, inputValue($event))"></textarea>
                  </div>
                  <div v-else-if="fieldType(field) === 'select'" class="field plugin-field" :data-help="field.description || undefined">
                    <label class="field-label" :for="`${fieldId(contribution, field)}-trigger`">{{ field.label }}<span v-if="field.required" class="req"> *</span></label>
                    <NxpSelect :id="fieldId(contribution, field)" :model-value="String(contributionValue(contribution, field.key) || '')" :options="contributionOptions(field)" :disabled="field.readOnly" :aria-label="field.label" @update:model-value="setContributionValue(contribution, field.key, $event)" />
                  </div>
                  <div v-else-if="fieldType(field) === 'multi-select'" class="field plugin-field" :data-help="field.description || undefined">
                    <label class="field-label" :for="`${fieldId(contribution, field)}-trigger`">{{ field.label }}<span v-if="field.required" class="req"> *</span></label>
                    <NxpSelect :id="fieldId(contribution, field)" multiple :model-value="contributionMultiValue(contribution, field)" :options="contributionOptions(field)" :disabled="field.readOnly" :aria-label="field.label" @update:model-value="setContributionValue(contribution, field.key, $event)" />
                  </div>
                  <div v-else-if="fieldType(field) === 'path' || fieldType(field) === 'file' || fieldType(field) === 'folder'" class="field plugin-field" :data-help="field.description || undefined">
                    <label class="field-label" :for="fieldId(contribution, field)">{{ field.label }}<span v-if="field.required" class="req"> *</span></label>
                    <NxpPathPicker :id="fieldId(contribution, field)" :model-value="contributionStringValue(contribution, field)" :kind="fieldType(field) === 'folder' ? 'folder' : 'file'" :placeholder="field.placeholder || undefined" :aria-label="field.label" :disabled="field.readOnly" @update:model-value="setContributionValue(contribution, field.key, $event)" @browse="browseContributionPath(contribution, field, $event)" />
                  </div>
                  <div v-else-if="fieldType(field) === 'secret'" class="field plugin-field plugin-secret-field" :data-help="field.description || undefined">
                    <label class="field-label" :for="fieldId(contribution, field)">{{ field.label }}<span v-if="field.required" class="req"> *</span></label>
                    <div class="plugin-secret-row"><input :id="fieldId(contribution, field)" type="password" :value="contributionStringValue(contribution, field)" :maxlength="field.maxLength || undefined" :placeholder="secretIsConfigured(contribution, field) ? t('users.secret.configured_placeholder', { set: t('common.set'), leaveBlank: t('common.leave_blank_to_keep').toLowerCase() }) : (field.placeholder || undefined)" :readonly="field.readOnly" @input="setSecretValue(contribution, field, inputValue($event))"><button v-if="secretIsConfigured(contribution, field) && !field.readOnly" class="tertiary" type="button" @click="clearSecret(contribution, field)">{{ t('users.clear') }}</button></div>
                  </div>
                  <div v-else-if="fieldType(field) === 'status'" class="field plugin-field" :data-help="field.description || undefined">
                    <span class="field-label">{{ field.label }}</span><span class="plugin-status-value">{{ String(contributionValue(contribution, field.key) || t('users.no_status')) }}</span>
                  </div>
                  <div v-else class="field plugin-field" :data-help="field.description || undefined">
                    <label class="field-label" :for="fieldId(contribution, field)">{{ field.label }}<span v-if="field.required" class="req"> *</span></label>
                    <input :id="fieldId(contribution, field)" :type="fieldType(field) === 'number' ? 'number' : fieldType(field) === 'url' ? 'url' : 'text'" :value="contributionStringValue(contribution, field)" :maxlength="field.maxLength || undefined" :placeholder="field.placeholder || undefined" :readonly="field.readOnly" @input="setContributionValue(contribution, field.key, inputValue($event))">
                  </div>
                </template>
              </div>
            </article>
            <div ref="globalSlotRoot" class="plugin-slot global-management-plugin-slot" data-plugin-slot="users.global.sections" data-plugin-anchor="users.global.sections" data-plugin-mode="user" :data-plugin-primary-id="globalDraft.userId" hidden></div>
          </section>
      </template>
      <template #footer>
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
      </template>
    </NxpModal>
    <NxpModal
      :open="Boolean(userDraft)"
      :closeable="false"
      :locked="true"
      :aria-label="t('users.user_management')"
      panel-class="secondary-surface"
      size="wide"
      data-locked
    >
      <template #header>
          <div><h3 class="modal-title">{{ t("users.user_management") }}</h3></div>
          <button
            class="icon-button modal-close"
            type="button"
            :aria-label="t('common.close')"
            @click.stop="closeUserManagement"
          >
            <NxpIcon name="close" />
          </button>
      </template>
      <template v-if="userDraft">
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
            <div v-if="userDraft.avatarUrl" class="user-avatar-setting">
              <span class="muted">{{ t("users.custom_avatar") }}</span>
              <button class="tertiary" type="button" @click.stop="removeAvatar(userDraft.id)">{{ t("users.remove_custom_avatar") }}</button>
            </div>
          </section>
          <section
            class="subsection user-binding-section"
            :class="{
              'um-section-expanding': Boolean(expandedBindingId),
              'um-binding-editing': bindingEditMode,
            }"
            data-testid="um-binding-section"
          >
            <div class="section-heading um-binding-section-heading">
              <div>
                <h3>{{ t("users.bound_script_instances") }}</h3>
                <p class="muted">{{ t("users.binding.settings_help") }}</p>
              </div>
              <button
                class="ghost sm um-binding-edit-toggle"
                type="button"
                :aria-pressed="bindingEditMode"
                :hidden="Boolean(expandedBindingId)"
                @click.stop="toggleBindingEdit"
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
                    <NxpEntityIcon :id="script.id" />
                    <span class="um-add-item-copy">
                      <strong>{{ script.name }}</strong>
                      <span v-if="script.pluginType" class="muted">{{ t("users.specialized_script") }}</span>
                    </span>
                    <span class="um-add-item-mark" aria-hidden="true"><NxpIcon name="check" /></span>
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
            <div v-if="userDraft.bindings?.length" v-sortable="{ canDrag: canReorderBindings, onDrop: reorderBindings }" class="um-bindings">
              <article
                v-for="binding in userDraft.bindings"
                :key="binding.scriptInstanceId"
                class="um-binding-card"
                :class="{
                  'is-expanded': expandedBindingId === binding.scriptInstanceId,
                  'is-binding-editing': bindingEditMode,
                  'is-unavailable': Boolean(bindingStatus(binding)),
                }"
                data-testid="um-binding-card"
                :data-dnd-id="binding.scriptInstanceId"
                :data-binding-id="binding.scriptInstanceId"
              >
                <div class="um-binding-head">
                  <span
                    class="drag-handle um-binding-drag-handle"
                    role="button"
                    :tabindex="bindingEditMode || expandedBindingId ? -1 : 0"
                    :aria-disabled="bindingEditMode || expandedBindingId ? 'true' : 'false'"
                    :aria-hidden="bindingEditMode || expandedBindingId ? 'true' : undefined"
                    :aria-label="t('common.reorder.keyboard_help')"
                    :title="bindingEditMode || expandedBindingId ? undefined : t('common.drag_to_reorder')"
                    ><NxpIcon name="grip" /></span
                  ><button
                    class="um-binding-toggle"
                    :class="{ 'is-unavailable': Boolean(bindingStatus(binding)) }"
                    type="button"
                    data-action="toggle-um-binding"
                    :disabled="bindingEditMode || Boolean(bindingStatus(binding))"
                    :aria-disabled="bindingEditMode || Boolean(bindingStatus(binding)) ? 'true' : undefined"
                    :title="bindingStatus(binding) || undefined"
                    :aria-expanded="
                      expandedBindingId === binding.scriptInstanceId
                    "
                    @click.stop="toggleBinding(binding)"
                  >
                    <NxpEntityIcon class="um-binding-ico" :id="binding.scriptInstanceId" /><span class="um-binding-copy"
                      ><strong class="um-binding-name">{{
                        bindingName(binding)
                      }}</strong
                      ><span class="um-binding-badges"
                        ><NxpBadge
                          v-if="bindingPluginStatus(binding)?.missing"
                          tone="bad"
                          >{{ t("common.plugin.unknown") }}</NxpBadge
                        ><NxpBadge
                          v-else-if="bindingPluginStatus(binding)?.specialized && !bindingPluginStatus(binding)?.available"
                          tone="warn"
                          >{{ t("common.plugin.unavailable_badge") }}</NxpBadge
                        ><NxpBadge
                          :tone="bindingEnabled(binding) ? 'ok' : 'muted'"
                          >{{ bindingEnabled(binding) ? t("users.enabled_badge") : t("common.disabled") }}</NxpBadge
                        ><NxpBadge :tone="bindingDaysTone(binding)">{{ bindingDaysLabel(binding) }}</NxpBadge
                        ></span
                      ></span
                    ><span class="um-binding-bottom-arrow" aria-hidden="true"
                      :data-direction="expandedBindingId === binding.scriptInstanceId ? 'down' : 'right'"
                      ><NxpIcon :name="expandedBindingId === binding.scriptInstanceId ? 'chevronDown' : 'chevronRight'"
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
                <Transition name="nxp-collapse">
                <div
                  v-if="expandedBindingId === binding.scriptInstanceId"
                  class="um-binding-body"
                >
                  <div class="um-binding-options">
                  <button class="um-edit-config" type="button" :class="{ 'is-unavailable': Boolean(bindingStatus(binding)) }" @click.stop="openConfigEdit(binding)">
                    <span class="um-edit-config-copy"><strong>{{ t("users.edit_configuration") }}</strong><span class="muted">{{ t("users.binding.config_open_help") }}</span></span><span class="um-edit-config-arrow" aria-hidden="true"><NxpIcon name="chevronRight" /></span>
                  </button>
                  <section class="um-binding-option-section">
                    <div class="section-heading">
                      <div>
                        <h4>{{ t("common.general") }}</h4>
                        <p class="muted">
                          {{ t("users.binding.status_help") }}
                        </p>
                      </div>
                    </div>
                    <NxpSwitchSetting
                      :label="t('common.enabled')"
                      :description="t('users.binding.run_days.zero_help')"
                      :model-value="bindingValue(binding, 'enabled') !== false"
                      :aria-label="t('common.enabled')"
                      :disabled="binding.locks?.general === true"
                      @update:model-value="setBindingValue(binding, 'enabled', $event)"
                    />
                    <div class="field">
                      <label
                        class="field-label"
                        :for="`um-${binding.scriptInstanceId}-run-days`"
                        >{{ t("common.run_days") }}</label>
                      <NxpNumberInput
                        :id="`um-${binding.scriptInstanceId}-run-days`"
                        :model-value="numericValue(bindingValue(binding, 'runDays'))"
                        :min="-1"
                        :max="maxRunDays()"
                        :placeholder="t('users.binding.run_days.input_help')"
                        :help="t('users.binding.run_days.help')"
                        :aria-label="t('common.run_days')"
                        :disabled="binding.locks?.general === true"
                        @update:model-value="setBindingValue(binding, 'runDays', numericValue($event))"
                      />
                    </div>
                    <div class="field">
                      <label
                        class="field-label"
                        :for="`um-${binding.scriptInstanceId}-max-success`"
                        >{{ t("users.maximum_successful_runs") }}</label>
                      <NxpNumberInput
                        :id="`um-${binding.scriptInstanceId}-max-success`"
                        :model-value="numericValue(bindingValue(binding, 'maxSuccessfulRunsPerDay'))"
                        :min="-1"
                        :max="maxSuccessfulRunsPerDay()"
                        :placeholder="t('users.unlimited_placeholder')"
                        :help="t('users.binding.daily_limit.help')"
                        :aria-label="t('users.maximum_successful_runs')"
                        :disabled="binding.locks?.general === true"
                        @update:model-value="setBindingValue(binding, 'maxSuccessfulRunsPerDay', numericValue($event))"
                      />
                    </div>
                    <p class="muted helper-copy">{{ t("users.binding.daily_limit.policy_help") }}</p>
                    <p v-if="bindingOverrideHelp(binding, 'general')" class="muted helper-copy um-override-helper">{{ bindingOverrideHelp(binding, 'general') }}</p>
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
                    <NxpSwitchSetting
                      :label="t('users.enable_notifications')"
                      :description="t('users.binding.status_notification_help')"
                      :model-value="bindingValue(binding, 'notifyEnabled') !== false"
                      :aria-label="t('users.enable_notifications')"
                      :disabled="binding.locks?.notification === true"
                      @update:model-value="setBindingValue(binding, 'notifyEnabled', $event)"
                    />
                    <div class="field" :data-help="t('users.binding.smtp_help')">
                      <label
                        class="field-label"
                        :for="`um-${binding.scriptInstanceId}-smtp`"
                        >{{ t("users.smtp_recipients") }}</label
                      ><input
                        :id="`um-${binding.scriptInstanceId}-smtp`"
                        :value="bindingValue(binding, 'smtpTo') || ''"
                        type="text"
                        :disabled="binding.locks?.notification === true"
                        :placeholder="t('users.binding.smtp_inherit_help')"
                        @input="
                          setBindingValue(binding, 'smtpTo', inputValue($event))
                        "
                      />
                    </div>
                    <p v-if="bindingOverrideHelp(binding, 'notification')" class="muted helper-copy um-override-helper">{{ bindingOverrideHelp(binding, 'notification') }}</p>
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
                      ><NxpPathPicker
                        :model-value="encodePrePost(PRE_ONLY_MARKER, bindingValue(binding, 'preRunOnceOnly'), bindingValue(binding, 'preRunScript'))"
                        kind="file"
                        :placeholder="t('users.pre_task_placeholder')"
                        :aria-label="t('users.before_task_script_path')"
                        :filter="t('users.script_file_filter')"
                        :help="t('users.pre_task_help')"
                        :disabled="binding.locks?.advanced === true"
                        @update:model-value="setBindingPath(binding, 'preRunScript', $event)"
                        @browse="browseBindingPath(binding, 'preRunScript', $event)"
                      />
                    </div>
                    <div class="field">
                      <label class="field-label">{{
                        t("users.after_task_script_path")
                      }}</label
                      ><NxpPathPicker
                        :model-value="encodePrePost(POST_FINAL_MARKER, bindingValue(binding, 'postRunOnFinalOnly'), bindingValue(binding, 'postRunScript'))"
                        kind="file"
                        :placeholder="t('users.post_task_placeholder')"
                        :aria-label="t('users.after_task_script_path')"
                        :filter="t('users.script_file_filter')"
                        :help="t('users.post_task_help')"
                        :disabled="binding.locks?.advanced === true"
                        @update:model-value="setBindingPath(binding, 'postRunScript', $event)"
                        @browse="browseBindingPath(binding, 'postRunScript', $event)"
                      />
                    </div>
                    <p v-if="bindingOverrideHelp(binding, 'advanced')" class="muted helper-copy um-override-helper">{{ bindingOverrideHelp(binding, 'advanced') }}</p>
                  </section>
                  </div>
                  <div class="plugin-slot user-binding-plugin-slot" data-plugin-slot="users.binding.sections" data-plugin-anchor="users.binding.sections" data-plugin-mode="binding" :data-plugin-primary-id="binding.scriptInstanceId" :data-plugin-secondary-id="userDraft.id" hidden></div>
                </div>
                </Transition>
              </article>
            </div>
            <NxpEmptyState
              v-else
              :title="t('users.no_script_instances_bound_yet')"
              :description="t('users.binding.add_help')"
            />
          </section>
      </template>
      <template #footer>
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
      </template>
    </NxpModal>
    <NxpModal
      :open="Boolean(configChooser)"
      :closeable="false"
      :locked="true"
      :aria-label="t('users.first_edit') + ' ' + t('users.edit_configuration')"
      panel-class="secondary-surface"
      data-locked
    >
      <template #header>
          <div><h3 class="modal-title">{{ t("users.first_edit") }} {{ t("users.edit_configuration") }}</h3></div>
          <button class="icon-button modal-close" type="button" :aria-label="t('common.close')" @click.stop="closeConfigEdit"><NxpIcon name="close" /></button>
      </template>
      <template v-if="configChooser">
          <p class="modal-copy">{{ t("users.config.edit_first", { script: configChooser.scriptName }) }}</p>
          <div class="first-edit-chooser">
            <button class="chooser-card" type="button" :disabled="!configChooser.freshAvailable" @click.stop="chooseConfigMode('fresh')">
              <strong>{{ t("users.fresh_configuration_file") }}</strong>
              <span class="muted">{{ configChooser.freshAvailable ? t("users.config.generated") : t("users.config.unavailable") }}</span>
            </button>
            <button class="chooser-card" type="button" @click.stop="chooseConfigMode('reuse')">
              <strong>{{ t("users.reuse_configuration_file") }}</strong>
              <span class="muted">{{ t("users.config.edit_existing") }}</span>
            </button>
          </div>
      </template>
      <template #footer><button class="ghost" type="button" @click.stop="closeConfigEdit">{{ t("common.cancel") }}</button></template>
    </NxpModal>
    <NxpModal
      :open="Boolean(configCandidates)"
      :closeable="false"
      :locked="true"
      :aria-label="t('users.take_over_configuration')"
      panel-class="secondary-surface"
      data-locked
    >
      <template #header>
          <div><h3 class="modal-title">{{ t("users.take_over_configuration") }}</h3></div>
          <button class="icon-button modal-close" type="button" :aria-label="t('common.close')" @click.stop="closeConfigEdit"><NxpIcon name="close" /></button>
      </template>
      <template v-if="configCandidates">
          <p class="modal-copy">{{ t("users.config.candidates_help") }}</p>
          <div class="first-edit-chooser">
            <button v-for="candidate in configCandidates.candidates" :key="candidate" class="chooser-card" type="button" @click.stop="chooseConfigCandidate(candidate)">
              <strong class="scroll-text"><span class="scroll-inner">{{ candidate }}</span></strong>
              <span class="muted">{{ t("users.config.candidate_used") }}</span>
            </button>
          </div>
      </template>
      <template #footer><button class="ghost" type="button" @click.stop="closeConfigEdit">{{ t("common.cancel") }}</button></template>
    </NxpModal>
    <NxpModal
      :open="Boolean(configEdit)"
      :closeable="false"
      :locked="true"
      :aria-label="t('users.config.edit_progress')"
      panel-class="secondary-surface"
      data-locked
    >
      <template #header>
          <div><h3 class="modal-title">{{ t("users.config.edit_progress") }}</h3></div>
          <button class="icon-button modal-close" type="button" :aria-label="t('common.close')" @click.stop="finishConfigEdit('cancel')"><NxpIcon name="close" /></button>
      </template>
      <template v-if="configEdit">
          <p class="modal-copy">{{ configEdit.mode === "fresh" ? t("users.config.edit_new_help") : configEdit.mode === "reuse" ? t("users.config.edit_existing_help") : t("users.config.edit_manual_help", { user: configEdit.userName, script: configEdit.scriptName }) }}</p>
      </template>
      <template #footer>
          <button class="primary" type="button" @click.stop="finishConfigEdit('done')">{{ t("common.complete") }}</button>
          <button class="ghost" type="button" @click.stop="finishConfigEdit('cancel')">{{ t("common.cancel") }}</button>
      </template>
    </NxpModal>
    <NxpModal
      :open="Boolean(deleteTarget)"
      :title="t('users.delete_user')"
      panel-class="secondary-surface"
      :close-label="t('common.close', {}, 'Close')"
      @close="closeDelete"
    >
      <p v-if="deleteTarget" class="modal-copy">
        {{ t("users.confirm.delete", { name: deleteTarget.name }) }}
      </p>
      <div class="field">
        <label class="field-label" for="gu-delete-name">{{
          t("users.confirm_username")
        }}</label
        ><input id="gu-delete-name" v-model="deleteName" type="text" />
      </div>
      <template #footer>
        <NxpButton class="ghost" type="button" @click="closeDelete">{{
          t("common.cancel")
        }}</NxpButton>
        <NxpButton
          class="danger solid"
          type="button"
          data-testid="confirm-delete-global-user"
          @click="confirmDelete"
          >{{ t("common.confirm_deletion") }}</NxpButton>
      </template>
    </NxpModal>
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
.users-page .plugin-contribution-fields {
  display: grid;
  gap: var(--space-3);
}
.users-page .plugin-field .muted {
  display: block;
  margin-top: var(--space-1);
}
.users-page .um-binding-options {
  display: grid;
  gap: var(--space-4);
}
.users-page .um-binding-toggle {
  min-width: 0;
  flex: 1;
}
.users-page .um-binding-copy {
  min-width: 0;
}
</style>
