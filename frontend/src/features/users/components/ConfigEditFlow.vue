<script setup lang="ts">
import { computed, ref } from "vue";
import { isAbortError } from "@legacy/core/api.js";
import { t } from "@legacy/core/i18n.js";
import { toast } from "@legacy/core/ui.js";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpModal from "../../../ui/primitives/NxpModal.vue";
import { editConfig, getEditConfigStatus } from "../services/usersApi";
import { buildConfigEditRequest } from "../utils/configEditRequest";

/** 配置编辑事务的唯一 owner：首次编辑入口（choose/candidate）、锁定编辑弹窗、
 *  done/cancel 与刷新后会话恢复都在此收敛。 */

interface ConfigEditDetails {
  userId: string;
  scriptId: string;
  userName: string;
  scriptName: string;
}
type ConfigEditItem = ConfigEditDetails & { mode: string };

const emit = defineEmits<{ changed: [userId: string] }>();

const configEdit = ref<ConfigEditItem | null>(null);
const configChooser = ref<(ConfigEditDetails & { freshAvailable: boolean }) | null>(null);
const configCandidates = ref<(ConfigEditDetails & { mode: string; inputName: string; candidates: string[] }) | null>(null);

const isOpen = computed(() => Boolean(configEdit.value || configChooser.value || configCandidates.value));

function errorText(reason: unknown) {
  return reason instanceof Error ? reason.message : String(reason);
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

async function begin(item: ConfigEditItem, inputOverride?: { name: string; value: string }) {
  const requesterWindowToken = createRequesterWindowToken();
  const previousTitle = document.title;
  document.title = `${t("users.nexuspipeline_core")} · ${requesterWindowToken}`;
  try {
    const request = buildConfigEditRequest(item.mode, inputOverride, requesterWindowToken);
    await waitForRequesterTitlePaint();
    await editConfig(item.userId, item.scriptId, request);
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
      configCandidates.value = { userId: item.userId, scriptId: item.scriptId, userName: item.userName, scriptName: item.scriptName, mode: item.mode, inputName: candidateData.inputName, candidates: candidateData.candidates };
      return;
    }
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  } finally {
    document.title = previousTitle;
  }
}

/** 从绑定卡片进入配置编辑：已有快照直接进入锁定弹窗，否则展示首次编辑选择。 */
async function open(details: ConfigEditDetails, freshAvailable: boolean) {
  try {
    const status = (await getEditConfigStatus(details.userId, details.scriptId)) as { hasSnapshot?: boolean } | null;
    if (status?.hasSnapshot) {
      await begin({ ...details, mode: "normal" });
      return;
    }
    configChooser.value = { ...details, freshAvailable };
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

/** 刷新或服务重启后直接恢复进行中的锁定编辑事务。 */
async function restore(session: ConfigEditItem) {
  await begin({ ...session });
}

async function chooseMode(mode: "fresh" | "reuse") {
  const chooser = configChooser.value;
  if (!chooser || (mode === "fresh" && !chooser.freshAvailable)) return;
  await begin({ userId: chooser.userId, scriptId: chooser.scriptId, userName: chooser.userName, scriptName: chooser.scriptName, mode });
}

async function chooseCandidate(candidate: string) {
  const chooser = configCandidates.value;
  if (!chooser) return;
  configCandidates.value = null;
  await begin({ userId: chooser.userId, scriptId: chooser.scriptId, userName: chooser.userName, scriptName: chooser.scriptName, mode: chooser.mode }, { name: chooser.inputName, value: candidate });
}

async function finish(action: "done" | "cancel") {
  const edit = configEdit.value;
  if (!edit) return;
  try {
    const result = (await editConfig(edit.userId, edit.scriptId, { action })) as { validation?: { toasts?: Array<{ message?: string; kind?: string }> } } | null;
    configEdit.value = null;
    toast(action === "done"
      ? (edit.mode === "fresh" ? t("users.config.snapshot_saved") : t("users.user_configuration_saved", { user: edit.userName }))
      : (edit.mode === "reuse" ? t("common.cancelled") : t("users.config.cancelled_restored")));
    for (const item of result?.validation?.toasts || []) if (item.message) toast(item.message, item.kind || "info");
    emit("changed", edit.userId);
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function close() {
  configEdit.value = null;
  configChooser.value = null;
  configCandidates.value = null;
}

defineExpose({ open, restore, finish, close, isOpen });
</script>

<template>
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
      <button class="icon-button modal-close" type="button" :aria-label="t('common.close')" @click.stop="close"><NxpIcon name="close" /></button>
    </template>
    <template v-if="configChooser">
      <p class="modal-copy">{{ t("users.config.edit_first", { script: configChooser.scriptName }) }}</p>
      <div class="first-edit-chooser">
        <button class="chooser-card" type="button" :disabled="!configChooser.freshAvailable" @click.stop="chooseMode('fresh')">
          <strong>{{ t("users.fresh_configuration_file") }}</strong>
          <span class="muted">{{ configChooser.freshAvailable ? t("users.config.generated") : t("users.config.unavailable") }}</span>
        </button>
        <button class="chooser-card" type="button" @click.stop="chooseMode('reuse')">
          <strong>{{ t("users.reuse_configuration_file") }}</strong>
          <span class="muted">{{ t("users.config.edit_existing") }}</span>
        </button>
      </div>
    </template>
    <template #footer><button class="ghost" type="button" @click.stop="close">{{ t("common.cancel") }}</button></template>
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
      <button class="icon-button modal-close" type="button" :aria-label="t('common.close')" @click.stop="close"><NxpIcon name="close" /></button>
    </template>
    <template v-if="configCandidates">
      <p class="modal-copy">{{ t("users.config.candidates_help") }}</p>
      <div class="first-edit-chooser">
        <button v-for="candidate in configCandidates.candidates" :key="candidate" class="chooser-card" type="button" @click.stop="chooseCandidate(candidate)">
          <strong class="scroll-text"><span class="scroll-inner">{{ candidate }}</span></strong>
          <span class="muted">{{ t("users.config.candidate_used") }}</span>
        </button>
      </div>
    </template>
    <template #footer><button class="ghost" type="button" @click.stop="close">{{ t("common.cancel") }}</button></template>
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
      <button class="icon-button modal-close" type="button" :aria-label="t('common.close')" @click.stop="finish('cancel')"><NxpIcon name="close" /></button>
    </template>
    <template v-if="configEdit">
      <p class="modal-copy">{{ configEdit.mode === "fresh" ? t("users.config.edit_new_help") : configEdit.mode === "reuse" ? t("users.config.edit_existing_help") : t("users.config.edit_manual_help", { user: configEdit.userName, script: configEdit.scriptName }) }}</p>
    </template>
    <template #footer>
      <button class="primary" type="button" @click.stop="finish('done')">{{ t("common.complete") }}</button>
      <button class="ghost" type="button" @click.stop="finish('cancel')">{{ t("common.cancel") }}</button>
    </template>
  </NxpModal>
</template>
