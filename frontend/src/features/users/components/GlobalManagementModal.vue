<script setup lang="ts">
import { nextTick, ref, watch } from "vue";
import { isAbortError } from "../../../platform/api";
import { state } from "../../../platform/page-state";
import { renderPluginSlot } from "@bridge/index";
import { disposePluginSlot } from "@bridge/index";
import { t } from "../../../platform/i18n";
import { toast } from "../../../platform/toast";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpModal from "../../../ui/primitives/NxpModal.vue";
import NxpNumberInput from "../../../ui/primitives/NxpNumberInput.vue";
import NxpPathPicker from "../../../ui/primitives/NxpPathPicker.vue";
import NxpSelect, { type NxpOption } from "../../../ui/primitives/NxpSelect.vue";
import NxpSwitchSetting from "../../../ui/composites/NxpSwitchSetting.vue";
import {
  browseNativeDialog,
  getGlobalContributions,
  getGlobalSettings,
  saveGlobalContribution,
  saveGlobalSettings,
} from "../services/usersApi";
import { encodePrePost, normalizeGlobalSettings, splitPrePost, PRE_ONLY_MARKER, POST_FINAL_MARKER } from "../utils/globalSettings";
import type { Contribution, GlobalField, GlobalSettings } from "../utils/globalSettings";

/** 全局管理弹窗：独立承担全局设置与插件贡献的加载、编辑、保存与清理。 */

const props = defineProps<{ userId: string; userName: string }>();
const emit = defineEmits<{ close: []; saved: [] }>();

const settings = ref<GlobalSettings>(normalizeGlobalSettings(null));
const contributions = ref<Contribution[]>([]);
const secretActions = ref<Record<string, "keep" | "set" | "clear">>({});
const slotRoot = ref<HTMLElement | null>(null);

const clone = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
const errorText = (reason: unknown) => (reason instanceof Error ? reason.message : String(reason));

function fieldKey(contribution: Contribution, field: GlobalField) {
  return `${contribution.pluginName || "plugin"}::${contribution.id || "settings"}::${field.key}`;
}
function fieldId(contribution: Contribution, field: GlobalField) {
  return `gm-plugin-${fieldKey(contribution, field).replace(/[^a-zA-Z0-9_-]/g, "-")}`;
}
function fieldType(field: GlobalField) {
  return String(field.type || "text").toLowerCase();
}

function maxRunDays() {
  const limits = (state as typeof state & { limits?: { maxRunDays?: number } }).limits;
  return Number(limits?.maxRunDays ?? 365);
}
function maxSuccessfulRunsPerDay() {
  const limits = (state as typeof state & { limits?: { maxSuccessfulRunsPerDay?: number } }).limits;
  return Number(limits?.maxSuccessfulRunsPerDay ?? 10);
}

function value(contribution: Contribution, key: string) {
  return contribution.values?.[key];
}
function setValue(contribution: Contribution, key: string, next: unknown) {
  contribution.values ||= {};
  contribution.values[key] = next;
}
function inputValue(event: Event) {
  return (event.target as HTMLInputElement | HTMLTextAreaElement).value;
}
function contributionStringValue(contribution: Contribution, field: GlobalField) {
  const current = value(contribution, field.key);
  return fieldType(field) === "secret" && current && typeof current === "object" ? "" : String(current ?? "");
}
function secretIsConfigured(contribution: Contribution, field: GlobalField) {
  const current = value(contribution, field.key);
  return Boolean(current && typeof current === "object" && (current as { configured?: boolean }).configured === true);
}
function setSecretValue(contribution: Contribution, field: GlobalField, next: string) {
  secretActions.value[fieldKey(contribution, field)] = next ? "set" : (secretIsConfigured(contribution, field) ? "keep" : "set");
  setValue(contribution, field.key, next);
}
function clearSecret(contribution: Contribution, field: GlobalField) {
  secretActions.value[fieldKey(contribution, field)] = "clear";
  setValue(contribution, field.key, "");
}
function contributionValuesForSave(contribution: Contribution) {
  const values: Record<string, unknown> = {};
  for (const field of contribution.fields || []) {
    const type = fieldType(field);
    const current = value(contribution, field.key);
    if (type === "secret") {
      const action = secretActions.value[fieldKey(contribution, field)] || (secretIsConfigured(contribution, field) ? "keep" : "set");
      values[field.key] = action === "set" ? { action, value: String(current || "") } : { action };
    } else if (type === "multi-select") {
      values[field.key] = Array.isArray(current) ? current.map(String) : [];
    } else {
      values[field.key] = current;
    }
  }
  return values;
}
function contributionOptions(field: GlobalField): NxpOption[] {
  return (field.options || []).map((option) =>
    typeof option === "string"
      ? { value: option, label: option }
      : { value: String(option.value || ""), label: String(option.label || option.value || "") },
  );
}
function contributionMultiValue(contribution: Contribution, field: GlobalField): string[] {
  const current = value(contribution, field.key);
  return Array.isArray(current) ? current.map(String) : [];
}
function validateContributionFields(contribution: Contribution) {
  for (const field of contribution.fields || []) {
    if (!field.required || field.readOnly || fieldType(field) === "status") continue;
    const current = value(contribution, field.key);
    if (fieldType(field) === "secret" && secretIsConfigured(contribution, field) && secretActions.value[fieldKey(contribution, field)] !== "clear") continue;
    const empty = fieldType(field) === "multi-select" ? !Array.isArray(current) || !current.length : !String(current ?? "").trim();
    if (empty) return false;
  }
  return true;
}

function setGlobalSwitch(section: keyof GlobalSettings, key: string, next: boolean) {
  (settings.value[section] as Record<string, unknown>)[key] = next;
}
function setGlobalInput(section: keyof GlobalSettings, key: string, next: string | number) {
  (settings.value[section] as Record<string, unknown>)[key] = next;
}
function setGlobalPath(key: "preRunScript" | "postRunScript", next: string) {
  const parsed = splitPrePost(key === "preRunScript" ? PRE_ONLY_MARKER : POST_FINAL_MARKER, next);
  setGlobalInput("advanced", key, parsed.value);
  setGlobalSwitch("advanced", key === "preRunScript" ? "preRunOnceOnly" : "postRunOnFinalOnly", parsed.onceOnly);
}
function numericValue(input: unknown, fallback = -1) {
  const parsed = Number(input);
  return Number.isFinite(parsed) ? parsed : fallback;
}

async function browseGlobalPath(key: "preRunScript" | "postRunScript", kind: "file" | "folder") {
  try {
    const result = (await browseNativeDialog({
      kind,
      title: key === "preRunScript" ? t("users.before_task_script_path") : t("users.after_task_script_path"),
      initialPath: String(settings.value.advanced[key] || "") || undefined,
      filter: t("users.script_file_filter"),
    })) as { path?: string } | null;
    if (result?.path) setGlobalPath(key, result.path);
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}
async function browseContributionPath(contribution: Contribution, field: GlobalField, kind: "file" | "folder") {
  try {
    const result = (await browseNativeDialog({
      kind,
      title: field.label,
      initialPath: contributionStringValue(contribution, field),
      filter: "",
    })) as { path?: string } | null;
    if (result?.path) setValue(contribution, field.key, result.path);
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

async function load() {
  const [nextSettings, contributionData] = (await Promise.all([
    getGlobalSettings(props.userId),
    getGlobalContributions(props.userId),
  ])) as [unknown, unknown];
  secretActions.value = {};
  settings.value = normalizeGlobalSettings(nextSettings);
  contributions.value = Array.isArray(contributionData) ? clone(contributionData as Contribution[]) : [];
  await nextTick();
  if (slotRoot.value) {
    await renderPluginSlot(slotRoot.value, "users.global.sections", { mode: "user", primaryId: props.userId });
  }
}

async function save() {
  try {
    if (contributions.value.some((contribution) => !validateContributionFields(contribution))) {
      toast(t("common.plugin.settings_required"), "error");
      return;
    }
    await saveGlobalSettings(props.userId, settings.value);
    for (const contribution of contributions.value) {
      await saveGlobalContribution(
        props.userId,
        contribution.pluginName || "",
        contribution.id || "",
        contributionValuesForSave(contribution),
      );
    }
    toast(t("users.global.saved"));
    emit("saved");
    close();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function close() {
  if (slotRoot.value) void disposePluginSlot(slotRoot.value);
  emit("close");
}

watch(
  () => props.userId,
  async (id) => {
    if (!id) return;
    try {
      await load();
    } catch (reason) {
      if (!isAbortError(reason)) toast(errorText(reason), "error");
      close();
    }
  },
  { immediate: true },
);
</script>

<template>
  <NxpModal
    :open="true"
    :closeable="false"
    :locked="true"
    :aria-label="t('users.global.title')"
    panel-class="secondary-surface"
    size="wide"
    data-locked
  >
    <template #header>
      <div><h3 class="modal-title">{{ t("users.global.title") }}</h3></div>
      <button class="icon-button modal-close" type="button" :aria-label="t('common.close')" @click.stop="close"><NxpIcon name="close" /></button>
    </template>
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
            :model-value="settings.general.syncEnabled"
            :aria-label="t('users.sync_general_settings')"
            @update:model-value="setGlobalSwitch('general', 'syncEnabled', $event)"
          />
          <NxpSwitchSetting
            :label="t('common.enabled')"
            :description="t('users.binding.run_days.zero_help')"
            :model-value="settings.general.enabled"
            :aria-label="t('common.enabled')"
            @update:model-value="setGlobalSwitch('general', 'enabled', $event)"
          />
        </div>
        <div class="field">
          <label class="field-label" for="gm-general-run-days">{{ t("common.run_days") }}</label>
          <NxpNumberInput
            id="gm-general-run-days"
            :model-value="settings.general.runDays"
            :min="-1"
            :max="maxRunDays()"
            :placeholder="t('users.global.run_days.placeholder')"
            :help="t('users.global.run_days.help')"
            :aria-label="t('common.run_days')"
            @update:model-value="setGlobalInput('general', 'runDays', numericValue($event))"
          />
        </div>
        <div class="field">
          <label class="field-label" for="gm-general-max-success">{{ t("users.maximum_successful_runs") }}</label>
          <NxpNumberInput
            id="gm-general-max-success"
            :model-value="settings.general.maxSuccessfulRunsPerDay"
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
            :model-value="settings.notification.syncEnabled"
            :aria-label="t('users.binding.sync_notifications')"
            @update:model-value="setGlobalSwitch('notification', 'syncEnabled', $event)"
          />
          <NxpSwitchSetting
            :label="t('users.enable_notifications')"
            :description="t('users.global.notification.enabled_help')"
            :model-value="settings.notification.notifyEnabled"
            :aria-label="t('users.enable_notifications')"
            @update:model-value="setGlobalSwitch('notification', 'notifyEnabled', $event)"
          />
        </div>
        <div class="field" :data-help="t('users.global.notification.smtp_help')">
          <label class="field-label" for="gm-notification-smtp">{{ t("users.smtp_recipients") }}</label>
          <input
            id="gm-notification-smtp"
            :value="settings.notification.smtpTo"
            type="text"
            :placeholder="t('users.binding.smtp_inherit_help')"
            @input="setGlobalInput('notification', 'smtpTo', inputValue($event))"
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
            :model-value="settings.advanced.syncEnabled"
            :aria-label="t('users.sync_advanced_settings')"
            @update:model-value="setGlobalSwitch('advanced', 'syncEnabled', $event)"
          />
        </div>
        <div class="field">
          <label class="field-label" for="gm-advanced-pre">{{ t("users.before_task_script_path") }}</label>
          <NxpPathPicker
            id="gm-advanced-pre"
            :model-value="encodePrePost(PRE_ONLY_MARKER, settings.advanced.preRunOnceOnly, settings.advanced.preRunScript)"
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
          <label class="field-label" for="gm-advanced-post">{{ t("users.after_task_script_path") }}</label>
          <NxpPathPicker
            id="gm-advanced-post"
            :model-value="encodePrePost(POST_FINAL_MARKER, settings.advanced.postRunOnFinalOnly, settings.advanced.postRunScript)"
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
    <section v-if="contributions.length" class="global-management-plugins">
      <div class="section-heading">
        <div>
          <h3>{{ t("common.plugin_settings") }}</h3>
          <p class="muted">{{ t("users.global.plugin_help") }}</p>
        </div>
      </div>
      <article
        v-for="contribution in contributions"
        :key="`${contribution.pluginName}-${contribution.id}`"
        class="global-management-plugin"
      >
        <div class="section-heading">
          <div>
            <h4>{{ contribution.pluginDisplayName || contribution.pluginName }}</h4>
            <strong v-if="contribution.title && contribution.title !== (contribution.pluginDisplayName || contribution.pluginName)">{{ contribution.title }}</strong>
            <p v-if="contribution.description" class="muted">{{ contribution.description }}</p>
          </div>
        </div>
        <div class="plugin-contribution-fields">
          <template v-for="field in contribution.fields || []" :key="field.key">
            <NxpSwitchSetting
              v-if="fieldType(field) === 'switch'"
              class="plugin-field"
              :label="`${field.label}${field.required ? ' *' : ''}`"
              :description="field.description"
              :model-value="value(contribution, field.key) === true"
              :aria-label="field.label"
              :disabled="field.readOnly"
              @update:model-value="setValue(contribution, field.key, $event)"
            />
            <div v-else-if="fieldType(field) === 'textarea'" class="field plugin-field" :data-help="field.description || undefined">
              <label class="field-label" :for="fieldId(contribution, field)">{{ field.label }}<span v-if="field.required" class="req"> *</span></label>
              <textarea :id="fieldId(contribution, field)" :value="contributionStringValue(contribution, field)" :maxlength="field.maxLength || undefined" :placeholder="field.placeholder || undefined" :readonly="field.readOnly" @input="setValue(contribution, field.key, inputValue($event))"></textarea>
            </div>
            <div v-else-if="fieldType(field) === 'select'" class="field plugin-field" :data-help="field.description || undefined">
              <label class="field-label" :for="`${fieldId(contribution, field)}-trigger`">{{ field.label }}<span v-if="field.required" class="req"> *</span></label>
              <NxpSelect :id="fieldId(contribution, field)" :model-value="String(value(contribution, field.key) || '')" :options="contributionOptions(field)" :disabled="field.readOnly" :aria-label="field.label" @update:model-value="setValue(contribution, field.key, $event)" />
            </div>
            <div v-else-if="fieldType(field) === 'multi-select'" class="field plugin-field" :data-help="field.description || undefined">
              <label class="field-label" :for="`${fieldId(contribution, field)}-trigger`">{{ field.label }}<span v-if="field.required" class="req"> *</span></label>
              <NxpSelect :id="fieldId(contribution, field)" multiple :model-value="contributionMultiValue(contribution, field)" :options="contributionOptions(field)" :disabled="field.readOnly" :aria-label="field.label" @update:model-value="setValue(contribution, field.key, $event)" />
            </div>
            <div v-else-if="fieldType(field) === 'path' || fieldType(field) === 'file' || fieldType(field) === 'folder'" class="field plugin-field" :data-help="field.description || undefined">
              <label class="field-label" :for="fieldId(contribution, field)">{{ field.label }}<span v-if="field.required" class="req"> *</span></label>
              <NxpPathPicker :id="fieldId(contribution, field)" :model-value="contributionStringValue(contribution, field)" :kind="fieldType(field) === 'folder' ? 'folder' : 'file'" :placeholder="field.placeholder || undefined" :aria-label="field.label" :disabled="field.readOnly" @update:model-value="setValue(contribution, field.key, $event)" @browse="browseContributionPath(contribution, field, $event)" />
            </div>
            <div v-else-if="fieldType(field) === 'secret'" class="field plugin-field plugin-secret-field" :data-help="field.description || undefined">
              <label class="field-label" :for="fieldId(contribution, field)">{{ field.label }}<span v-if="field.required" class="req"> *</span></label>
              <div class="plugin-secret-row"><input :id="fieldId(contribution, field)" type="password" :value="contributionStringValue(contribution, field)" :maxlength="field.maxLength || undefined" :placeholder="secretIsConfigured(contribution, field) ? t('users.secret.configured_placeholder', { set: t('common.set'), leaveBlank: t('common.leave_blank_to_keep').toLowerCase() }) : (field.placeholder || undefined)" :readonly="field.readOnly" @input="setSecretValue(contribution, field, inputValue($event))"><button v-if="secretIsConfigured(contribution, field) && !field.readOnly" class="tertiary" type="button" @click="clearSecret(contribution, field)">{{ t('users.clear') }}</button></div>
            </div>
            <div v-else-if="fieldType(field) === 'status'" class="field plugin-field" :data-help="field.description || undefined">
              <span class="field-label">{{ field.label }}</span><span class="plugin-status-value">{{ String(value(contribution, field.key) || t('users.no_status')) }}</span>
            </div>
            <div v-else class="field plugin-field" :data-help="field.description || undefined">
              <label class="field-label" :for="fieldId(contribution, field)">{{ field.label }}<span v-if="field.required" class="req"> *</span></label>
              <input :id="fieldId(contribution, field)" :type="fieldType(field) === 'number' ? 'number' : fieldType(field) === 'url' ? 'url' : 'text'" :value="contributionStringValue(contribution, field)" :maxlength="field.maxLength || undefined" :placeholder="field.placeholder || undefined" :readonly="field.readOnly" @input="setValue(contribution, field.key, inputValue($event))">
            </div>
          </template>
        </div>
      </article>
      <div ref="slotRoot" class="plugin-slot global-management-plugin-slot" data-plugin-slot="users.global.sections" data-plugin-anchor="users.global.sections" data-plugin-mode="user" :data-plugin-primary-id="props.userId" hidden></div>
    </section>
    <template #footer>
      <button class="primary" type="button" @click.stop="save">{{ t("common.save") }}</button>
      <button class="ghost" type="button" @click.stop="close">{{ t("common.cancel") }}</button>
    </template>
  </NxpModal>
</template>
