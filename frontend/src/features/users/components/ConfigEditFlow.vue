<script setup lang="ts">
import { t } from "../../../platform/i18n";
import { toast } from "../../../platform/toast";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpModal from "../../../ui/primitives/NxpModal.vue";
import { editConfig, getEditConfigStatus, listEditSessions } from "../services/usersApi";
import { useConfigEditFlow } from "../composables/useConfigEditFlow";

/** 配置编辑事务的唯一 owner：首次编辑入口（choose/candidate）、锁定编辑弹窗、
 *  done/cancel 与刷新后会话恢复都在此收敛；事务状态机在 useConfigEditFlow 中实现。 */

const emit = defineEmits<{ changed: [userId: string] }>();

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

const flow = useConfigEditFlow({
  getStatus: (userId, scriptId) => getEditConfigStatus(userId, scriptId) as Promise<{ hasSnapshot?: boolean } | null>,
  start: (userId, scriptId, request) => editConfig(userId, scriptId, request),
  finish: (userId, scriptId, action) => editConfig(userId, scriptId, { action }) as Promise<{ validation?: { toasts?: Array<{ message?: string; kind?: string }> } } | null>,
  listSessions: () => listEditSessions(),
  createRequesterWindowToken,
  waitForRequesterTitlePaint,
  getDocumentTitle: () => document.title,
  setDocumentTitle: (title) => { document.title = title; },
  notify: (message, kind) => {
    if (kind === "error") toast(message, "error");
    else toast(message);
  },
  translate: (key, args) => t(key, args),
  onTransactionChanged: (userId) => emit("changed", userId),
});

const { configEdit, configChooser, configCandidates, isOpen } = flow;

defineExpose({
  open: flow.open,
  restore: flow.restore,
  restoreExisting: flow.restoreExisting,
  finish: flow.finish,
  close: flow.close,
  isOpen,
});
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
      <button class="icon-button modal-close" type="button" :aria-label="t('common.close')" @click.stop="flow.close"><NxpIcon name="close" /></button>
    </template>
    <template v-if="configChooser">
      <p class="modal-copy">{{ t("users.config.edit_first", { script: configChooser.scriptName }) }}</p>
      <div class="first-edit-chooser">
        <button class="chooser-card" type="button" :disabled="!configChooser.freshAvailable" @click.stop="flow.chooseMode('fresh')">
          <strong>{{ t("users.fresh_configuration_file") }}</strong>
          <span class="muted">{{ configChooser.freshAvailable ? t("users.config.generated") : t("users.config.unavailable") }}</span>
        </button>
        <button class="chooser-card" type="button" @click.stop="flow.chooseMode('reuse')">
          <strong>{{ t("users.reuse_configuration_file") }}</strong>
          <span class="muted">{{ t("users.config.edit_existing") }}</span>
        </button>
      </div>
    </template>
    <template #footer><button class="ghost" type="button" @click.stop="flow.close">{{ t("common.cancel") }}</button></template>
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
      <button class="icon-button modal-close" type="button" :aria-label="t('common.close')" @click.stop="flow.close"><NxpIcon name="close" /></button>
    </template>
    <template v-if="configCandidates">
      <p class="modal-copy">{{ t("users.config.candidates_help") }}</p>
      <div class="first-edit-chooser">
        <button v-for="candidate in configCandidates.candidates" :key="candidate" class="chooser-card" type="button" @click.stop="flow.chooseCandidate(candidate)">
          <strong class="scroll-text"><span class="scroll-inner">{{ candidate }}</span></strong>
          <span class="muted">{{ t("users.config.candidate_used") }}</span>
        </button>
      </div>
    </template>
    <template #footer><button class="ghost" type="button" @click.stop="flow.close">{{ t("common.cancel") }}</button></template>
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
      <button class="icon-button modal-close" type="button" :aria-label="t('common.close')" @click.stop="flow.finish('cancel')"><NxpIcon name="close" /></button>
    </template>
    <template v-if="configEdit">
      <p class="modal-copy">{{ configEdit.mode === "fresh" ? t("users.config.edit_new_help") : configEdit.mode === "reuse" ? t("users.config.edit_existing_help") : t("users.config.edit_manual_help", { user: configEdit.userName, script: configEdit.scriptName }) }}</p>
    </template>
    <template #footer>
      <button class="primary" type="button" @click.stop="flow.finish('done')">{{ t("common.complete") }}</button>
      <button class="ghost" type="button" @click.stop="flow.finish('cancel')">{{ t("common.cancel") }}</button>
    </template>
  </NxpModal>
</template>
