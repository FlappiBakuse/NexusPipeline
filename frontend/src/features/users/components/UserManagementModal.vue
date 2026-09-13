<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from "vue";
import { isAbortError } from "../../../platform/api";
import { state } from "../../../platform/page-state";
import { scriptPluginStatus, scriptPluginUnavailableMessage } from "../../scripts/utils/pluginStatus";
import { renderPluginSlot } from "@bridge/index";
import { disposePluginSlot } from "@bridge/index";
import { t } from "../../../platform/i18n";
import { clearFieldError, setRequiredFieldError, toast } from "../../../platform/toast";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpEmptyState from "../../../ui/primitives/NxpEmptyState.vue";
import NxpEntityIcon from "../../../ui/primitives/NxpEntityIcon.vue";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpModal from "../../../ui/primitives/NxpModal.vue";
import NxpPathPicker from "../../../ui/primitives/NxpPathPicker.vue";
import NxpNumberInput from "../../../ui/primitives/NxpNumberInput.vue";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpTextArea from "../../../ui/primitives/NxpTextArea.vue";
import NxpTextInput from "../../../ui/primitives/NxpTextInput.vue";
import NxpSwitchSetting from "../../../ui/composites/NxpSwitchSetting.vue";
import { vSortable } from "../../../ui/sortable";
import {
  addBinding,
  browseNativeDialog,
  getUser,
  removeAvatar as removeAvatarRequest,
  removeBinding as removeBindingRequest,
  reorderBindings as reorderBindingsRequest,
  saveBinding,
  updateUser,
} from "../services/usersApi";
import type { Binding, BindingEffective, BindingLocks, Plugin, Script, User } from "../utils/userTypes";
import { POST_FINAL_MARKER, PRE_ONLY_MARKER, encodePrePost, splitPrePost } from "../utils/globalSettings";

/** 用户管理弹窗：承担用户草稿、绑定增删改、绑定排序、头像与保存事务。 */

const props = defineProps<{
  user: User;
  scripts: Script[];
  plugins: Plugin[];
  users: User[];
}>();
const emit = defineEmits<{
  close: [];
  saved: [];
  "open-config": [details: { userId: string; scriptId: string; userName: string; scriptName: string; freshAvailable: boolean }];
}>();

const clone = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T;

const draft = ref<User>(clone({ ...props.user, bindings: props.user.bindings || [] }));
const expandedBindingId = ref<string | null>(null);
const bindingEditMode = ref(false);
const addBindingOpen = ref(false);
const selectedBindingIds = ref<string[]>([]);
const root = ref<HTMLElement | null>(null);
const bindingMotionDuration = 180;

const availableBindingScripts = computed(() => {
  const bound = new Set((draft.value.bindings || []).map((binding) => binding.scriptInstanceId));
  return props.scripts
    .slice()
    .sort((a, b) => (a.index ?? 0) - (b.index ?? 0))
    .filter((script) => {
      if (bound.has(script.id)) return false;
      const status = scriptPluginStatus(script, props.plugins);
      return !status.specialized || status.available;
    });
});

const errorText = (reason: unknown) => (reason instanceof Error ? reason.message : String(reason));

function bindingName(binding: Binding) {
  return (
    binding.scriptName ||
    props.scripts.find((script) => script.id === binding.scriptInstanceId)?.name ||
    t("users.script_instance_not_found")
  );
}
function bindingStatus(binding: Binding) {
  const script = props.scripts.find((item) => item.id === binding.scriptInstanceId);
  return script ? scriptPluginUnavailableMessage(script, props.plugins) : "";
}
function bindingPluginStatus(binding: Binding) {
  const script = props.scripts.find((item) => item.id === binding.scriptInstanceId);
  return script ? scriptPluginStatus(script, props.plugins) : null;
}
function bindingValue(binding: Binding, field: keyof BindingEffective) {
  const category = ["enabled", "runDays", "maxSuccessfulRunsPerDay"].includes(field)
    ? "general"
    : ["notifyEnabled", "smtpTo"].includes(field)
      ? "notification"
      : "advanced";
  if (binding.locks?.[category as keyof BindingLocks] && binding.effective && field in binding.effective)
    return binding.effective[field];
  return binding[field as keyof Binding] as unknown;
}
function setBindingValue(binding: Binding, field: keyof Binding, value: unknown) {
  const category = ["enabled", "runDays", "maxSuccessfulRunsPerDay"].includes(field)
    ? "general"
    : ["notifyEnabled", "smtpTo"].includes(field)
      ? "notification"
      : "advanced";
  if (binding.locks?.[category as keyof BindingLocks]) return;
  (binding as unknown as Record<string, unknown>)[field] = value;
}
function bindingEnabled(binding: Binding) {
  return bindingValue(binding, "enabled") !== false && bindingDays(binding) !== 0;
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
function bindingOverrideHelp(binding: Binding, category: keyof BindingLocks) {
  return binding.locks?.[category]
    ? t("users.binding.global_override.help", {
        global: t("users.global.title"),
        script: t("common.script_instance"),
      })
    : "";
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
function setBindingPath(binding: Binding, key: "preRunScript" | "postRunScript", value: string) {
  const parsed = splitPrePost(key === "preRunScript" ? PRE_ONLY_MARKER : POST_FINAL_MARKER, value);
  setBindingValue(binding, key, parsed.value);
  setBindingValue(binding, key === "preRunScript" ? "preRunOnceOnly" : "postRunOnFinalOnly", parsed.onceOnly);
}
async function browseBindingPath(binding: Binding, key: "preRunScript" | "postRunScript", kind: "file" | "folder") {
  try {
    const result = (await browseNativeDialog({
      kind,
      title: key === "preRunScript" ? t("users.before_task_script_path") : t("users.after_task_script_path"),
      initialPath: String(bindingValue(binding, key) || "") || undefined,
      filter: t("users.script_file_filter"),
    })) as { path?: string } | null;
    if (result?.path) setBindingPath(binding, key, result.path);
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function captureBindingRects() {
  const cards = root.value?.querySelectorAll<HTMLElement>('[data-testid="um-binding-card"]') || [];
  return new Map(
    Array.from(cards)
      .map((card) => [card.dataset.bindingId || "", card.getBoundingClientRect()] as const)
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
        { duration: bindingMotionDuration, easing: "cubic-bezier(.2,.8,.2,1)", fill: "both" },
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
  expandedBindingId.value = expandedBindingId.value === binding.scriptInstanceId ? null : binding.scriptInstanceId;
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
  const current = draft.value;
  if (!current || !canReorderBindings()) return;
  const byId = new Map((current.bindings || []).map((binding) => [binding.scriptInstanceId, binding]));
  const next = ids.map((id) => byId.get(id)).filter((binding): binding is NonNullable<User["bindings"]>[number] => Boolean(binding));
  if (next.length !== (current.bindings || []).length || next.every((binding, index) => binding.scriptInstanceId === current.bindings?.[index]?.scriptInstanceId)) return;
  current.bindings = next;
  try {
    await reorderBindingsRequest(current.id, next.map((item) => item.scriptInstanceId));
    toast(t("users.bound_script_order_saved"));
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
    await refresh();
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
  const current = draft.value;
  if (!current || !selectedBindingIds.value.length) {
    toast(t("users.binding.choose_one"), "error");
    return;
  }
  try {
    for (const scriptId of selectedBindingIds.value) await addBinding(current.id, scriptId);
    draft.value = clone((await getUser(current.id)) as User);
    addBindingOpen.value = false;
    selectedBindingIds.value = [];
    toast(t("users.script_binding_added"));
    await paintBindingSlots();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}
async function removeBinding(binding: Binding) {
  const current = draft.value;
  if (!current) return;
  try {
    await removeBindingRequest(current.id, binding.scriptInstanceId);
    draft.value = clone((await getUser(current.id)) as User);
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
      secondaryId: draft.value?.id || "",
    });
  }
}
async function refresh() {
  try {
    draft.value = clone((await getUser(props.user.id)) as User);
    await paintBindingSlots();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

async function removeAvatar() {
  try {
    await removeAvatarRequest(props.user.id);
    toast(t("users.avatar.default_restored"));
    await refresh();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function openConfigEdit(binding: Binding) {
  const current = draft.value;
  if (!current) return;
  const unavailableMessage = bindingStatus(binding);
  if (unavailableMessage) {
    toast(unavailableMessage, "error");
    return;
  }
  const script = props.scripts.find((item) => item.id === binding.scriptInstanceId);
  const plugin = props.plugins.find((item) => item.name === script?.pluginType) as (Plugin & { noFreshConfig?: boolean }) | undefined;
  emit("open-config", {
    userId: current.id,
    scriptId: binding.scriptInstanceId,
    userName: current.name,
    scriptName: bindingName(binding),
    freshAvailable: plugin?.noFreshConfig !== true,
  });
}

async function save() {
  const current = draft.value;
  if (!current) return;
  const name = current.name.trim();
  clearFieldError("um-name");
  if (!name) {
    setRequiredFieldError("um-name");
    toast(t("users.validation.username_required"), "error");
    return;
  }
  if (new TextEncoder().encode(name).length > 64) {
    setRequiredFieldError("um-name");
    toast(t("users.validation.username_length", { bytes: 64 }), "error");
    return;
  }
  if (props.users.some((user) => user.id !== current.id && user.name.toLocaleLowerCase() === name.toLocaleLowerCase())) {
    setRequiredFieldError("um-name");
    toast(t("users.validation.username_duplicate"), "error");
    return;
  }
  const remark = String(current.remark || "").trim();
  if (new TextEncoder().encode(remark).length > 512) {
    toast(t("users.validation.remark_length", { bytes: 512 }), "error");
    return;
  }
  try {
    await updateUser(current.id, { name, remark });
    for (const binding of current.bindings || []) await saveBinding(current.id, binding as unknown as Record<string, unknown>);
    toast(t("users.user_settings_saved"));
    emit("saved");
    close();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function close() {
  const slots = root.value?.querySelectorAll<HTMLElement>('[data-plugin-slot="users.binding.sections"]') || [];
  for (const slot of slots) void disposePluginSlot(slot);
  emit("close");
}

defineExpose({ refresh });

onMounted(() => {
  void paintBindingSlots();
});
</script>

<template>
  <NxpModal
    :locked="true"
    :open="true"
    :title="t('users.user_management')"
    :aria-label="t('users.user_management')"
    panel-class="secondary-surface"
    size="wide"
    :close-label="t('common.close')"
    @close="close"
  >
    <section ref="root" class="user-management-surface">
      <section class="user-management-settings">
        <div class="field">
          <label class="field-label" for="um-name">{{ t("users.user_name") }} <span class="req">*</span></label>
          <NxpTextInput id="um-name" v-model="draft.name" :aria-label="t('users.user_name')" @update:model-value="clearFieldError('um-name')" />
        </div>
        <div class="field">
          <label class="field-label" for="um-remark">{{ t("users.remark") }}</label>
          <NxpTextArea id="um-remark" class="form-textarea" v-model="draft.remark" :rows="3" :aria-label="t('users.remark')" />
        </div>
        <div v-if="draft.avatarUrl" class="user-avatar-setting">
          <span class="muted">{{ t("users.custom_avatar") }}</span>
          <NxpButton class="tertiary" type="button" @click.stop="removeAvatar">{{ t("users.remove_custom_avatar") }}</NxpButton>
        </div>
      </section>
      <section
        class="subsection user-binding-section"
        :class="{ 'um-section-expanding': Boolean(expandedBindingId), 'um-binding-editing': bindingEditMode }"
        data-testid="um-binding-section"
      >
        <div class="section-heading um-binding-section-heading">
          <div>
            <h3>{{ t("users.bound_script_instances") }}</h3>
            <p class="muted">{{ t("users.binding.settings_help") }}</p>
          </div>
          <button class="ghost sm um-binding-edit-toggle" type="button" :aria-pressed="bindingEditMode" :hidden="Boolean(expandedBindingId)" @click.stop="toggleBindingEdit">
            {{ bindingEditMode ? t("users.done_editing") : t("users.edit_bindings") }}
          </button>
        </div>
        <div class="um-add-area" :data-open="addBindingOpen ? '' : undefined">
          <button class="um-add-script" type="button" data-testid="um-add-script" :aria-expanded="addBindingOpen" @click.stop="toggleAddBindings">
            <NxpIcon name="plus" /> <span>{{ t("users.add_script") }}</span>
          </button>
          <div v-if="addBindingOpen" class="um-add-panel secondary-surface" data-testid="um-add-panel">
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
            <NxpEmptyState v-else :title="t('users.no_script_instances_can_be_added')" :description="t('users.binding.all_bound')" />
            <div class="um-add-actions">
              <NxpButton class="ghost" type="button" @click.stop="addBindingOpen = false">{{ t("common.cancel") }}</NxpButton>
              <NxpButton class="primary" type="button" data-testid="um-add-confirm" @click.stop="addBindings">{{ t("common.confirm") }}</NxpButton>
            </div>
          </div>
        </div>
        <div v-if="draft.bindings?.length" v-sortable="{ axis: 'both', canDrag: canReorderBindings, onDrop: reorderBindings }" class="um-bindings">
          <article
            v-for="binding in draft.bindings"
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
              ><NxpIcon name="grip" /></span>
              <button
                class="um-binding-toggle"
                :class="{ 'is-unavailable': Boolean(bindingStatus(binding)) }"
                type="button"
                data-action="toggle-um-binding"
                :disabled="bindingEditMode || Boolean(bindingStatus(binding))"
                :aria-disabled="bindingEditMode || Boolean(bindingStatus(binding)) ? 'true' : undefined"
                :title="bindingStatus(binding) || undefined"
                :aria-expanded="expandedBindingId === binding.scriptInstanceId"
                @click.stop="toggleBinding(binding)"
              >
                <NxpEntityIcon class="um-binding-ico" :id="binding.scriptInstanceId" /><span class="um-binding-copy"
                  ><strong class="um-binding-name">{{ bindingName(binding) }}</strong
                  ><span class="um-binding-badges"
                    ><NxpBadge v-if="bindingPluginStatus(binding)?.missing" tone="bad">{{ t("common.plugin.unknown") }}</NxpBadge
                    ><NxpBadge v-else-if="bindingPluginStatus(binding)?.specialized && !bindingPluginStatus(binding)?.available" tone="warn">{{ t("common.plugin.unavailable_badge") }}</NxpBadge
                    ><NxpBadge :tone="bindingEnabled(binding) ? 'ok' : 'muted'">{{ bindingEnabled(binding) ? t("users.enabled_badge") : t("common.disabled") }}</NxpBadge
                    ><NxpBadge :tone="bindingDaysTone(binding)">{{ bindingDaysLabel(binding) }}</NxpBadge
                  ></span
                ></span>
                <span class="um-binding-bottom-arrow" aria-hidden="true" :data-direction="expandedBindingId === binding.scriptInstanceId ? 'down' : 'right'">
                  <NxpIcon :name="expandedBindingId === binding.scriptInstanceId ? 'chevronDown' : 'chevronRight'" />
                </span>
              </button>
              <button class="danger um-binding-remove" type="button" data-testid="um-remove-binding" @click.stop="removeBinding(binding)">{{ t("users.remove_binding") }}</button>
            </div>
            <Transition name="nxp-collapse">
              <div v-if="expandedBindingId === binding.scriptInstanceId" class="um-binding-body">
                <div class="um-binding-options">
                  <button class="um-edit-config" type="button" :class="{ 'is-unavailable': Boolean(bindingStatus(binding)) }" @click.stop="openConfigEdit(binding)">
                    <span class="um-edit-config-copy"><strong>{{ t("users.edit_configuration") }}</strong><span class="muted">{{ t("users.binding.config_open_help") }}</span></span><span class="um-edit-config-arrow" aria-hidden="true"><NxpIcon name="chevronRight" /></span>
                  </button>
                  <section class="um-binding-option-section">
                    <div class="section-heading">
                      <div>
                        <h4>{{ t("common.general") }}</h4>
                        <p class="muted">{{ t("users.binding.status_help") }}</p>
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
                      <label class="field-label" :for="`um-${binding.scriptInstanceId}-run-days`">{{ t("common.run_days") }}</label>
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
                      <label class="field-label" :for="`um-${binding.scriptInstanceId}-max-success`">{{ t("users.maximum_successful_runs") }}</label>
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
                        <p class="muted">{{ t("users.binding.result_notification_help") }}</p>
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
                      <label class="field-label" :for="`um-${binding.scriptInstanceId}-smtp`">{{ t("users.smtp_recipients") }}</label>
                      <NxpTextInput
                        :id="`um-${binding.scriptInstanceId}-smtp`"
                        :model-value="String(bindingValue(binding, 'smtpTo') || '')"
                        type="text"
                        :disabled="binding.locks?.notification === true"
                        :placeholder="t('users.binding.smtp_inherit_help')"
                        :aria-label="t('users.smtp_recipients')"
                        @update:model-value="setBindingValue(binding, 'smtpTo', $event)"
                      />
                    </div>
                    <p v-if="bindingOverrideHelp(binding, 'notification')" class="muted helper-copy um-override-helper">{{ bindingOverrideHelp(binding, 'notification') }}</p>
                  </section>
                  <section class="um-binding-option-section">
                    <div class="section-heading">
                      <div>
                        <h4>{{ t("users.advanced") }}</h4>
                        <p class="muted">{{ t("users.binding.lifecycle_help") }}</p>
                      </div>
                    </div>
                    <div class="field">
                      <label class="field-label">{{ t("users.before_task_script_path") }}</label>
                      <NxpPathPicker
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
                      <label class="field-label">{{ t("users.after_task_script_path") }}</label>
                      <NxpPathPicker
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
                <div class="plugin-slot user-binding-plugin-slot" data-plugin-slot="users.binding.sections" data-plugin-anchor="users.binding.sections" data-plugin-mode="binding" :data-plugin-primary-id="binding.scriptInstanceId" :data-plugin-secondary-id="draft.id" hidden></div>
              </div>
            </Transition>
          </article>
        </div>
        <NxpEmptyState v-else :title="t('users.no_script_instances_bound_yet')" :description="t('users.binding.add_help')" />
      </section>
    </section>
    <template #footer>
      <NxpButton class="primary" type="button" @click.stop="save">{{ t("common.save") }}</NxpButton>
      <NxpButton class="ghost" type="button" data-action="close-modal" @click.stop="close">{{ t("common.cancel") }}</NxpButton>
    </template>
  </NxpModal>
</template>
