<script setup lang="ts">
import { onBeforeUnmount, onMounted } from "vue";
import { t } from "../../../platform/i18n";
import { toast } from "../../../platform/toast";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpModal from "../../../ui/primitives/NxpModal.vue";
import { editConfig, getEditConfigStatus, listEditSessions, listUsers, listScripts } from "../services/usersApi";
import { useConfigEditFlow } from "../composables/useConfigEditFlow";
import { getCapabilities } from "../../../platform/client-capabilities";
import { useOperationAccess, accessReason } from "../../../platform/operation-access";
const { allowed: nativeEditAllowed, reason: nativeEditReason } = useOperationAccess("nativeConfigEditor");

/** 配置编辑事务的唯一 owner：首次编辑入口（choose/candidate）、锁定编辑弹窗、
 *  done/cancel 与刷新后会话恢复都在此收敛；事务状态机在 useConfigEditFlow 中实现。 */

const emit = defineEmits<{ changed: [userId: string] }>();
const controller = new AbortController();
let disposed = false;
async function requireNative() {
  const decision = (await getCapabilities(controller.signal)).operations.nativeConfigEditor;
  if (!decision.allowed) throw new Error(accessReason(decision.denyReason!));
}

const flow = useConfigEditFlow({
  getStatus: (userId, scriptId) => getEditConfigStatus(userId, scriptId) as Promise<{ hasSnapshot?: boolean } | null>,
  start: async (userId, scriptId, request) => { await requireNative(); return editConfig(userId, scriptId, request, controller.signal); },
  finish: (userId, scriptId, action) => editConfig(userId, scriptId, { action }, controller.signal) as Promise<{ validation?: { toasts?: Array<{ message?: string; kind?: string }> } } | null>,
  listSessions: () => listEditSessions(controller.signal),
  notify: (message, kind) => {
    if (kind === "error") toast(message, "error");
    else toast(message);
  },
  translate: (key, args) => t(key, args),
  onTransactionChanged: (userId) => { emit("changed", userId); void restoreActiveSession(); },
});

const { configEdit, configChooser, configCandidates, finishingAction, isOpen } = flow;

async function restoreActiveSession() {
  try {
    const [users, scripts] = await Promise.all([listUsers(controller.signal), listScripts(controller.signal)]);
    await flow.restoreExisting(users, scripts, () => !disposed && !isOpen.value && nativeEditAllowed.value);
  } catch {
    // Host 会话仍持有事务；读取失败不发起第二次编辑或自动取消。
  }
}
onMounted(() => { void restoreActiveSession(); });
onBeforeUnmount(() => { disposed = true; controller.abort(); });

defineExpose({
  open: async (...args: Parameters<typeof flow.open>) => { try { await requireNative(); await flow.open(...args); } catch (error) { toast(String((error as Error).message), "error"); } },
  restore: flow.restore,
  restoreExisting: flow.restoreExisting,
  finish: flow.finish,
  close: flow.close,
  isOpen,
});
</script>

<template>
  <NxpModal
    :locked="true"
    :open="Boolean(configChooser)"
    :title="`${t('users.first_edit')} ${t('users.edit_configuration')}`"
    :aria-label="t('users.first_edit') + ' ' + t('users.edit_configuration')"
    panel-class="secondary-surface"
    :close-label="t('common.close')"
    @close="flow.close"
  >
    <template v-if="configChooser">
      <p class="modal-copy">{{ t("users.config.edit_first", { script: configChooser.scriptName }) }}</p>
      <div class="first-edit-chooser">
        <NxpButton class="chooser-card" type="button" :disabled="!configChooser.freshAvailable" @click.stop="flow.chooseMode('fresh')">
          <strong>{{ t("users.fresh_configuration_file") }}</strong>
          <span class="muted">{{ configChooser.freshAvailable ? t("users.config.generated") : t("users.config.unavailable") }}</span>
        </NxpButton>
        <NxpButton class="chooser-card" type="button" @click.stop="flow.chooseMode('reuse')">
          <strong>{{ t("users.reuse_configuration_file") }}</strong>
          <span class="muted">{{ t("users.config.edit_existing") }}</span>
        </NxpButton>
      </div>
    </template>
    <template #footer><NxpButton class="ghost" type="button" @click.stop="flow.close">{{ t("common.cancel") }}</NxpButton></template>
  </NxpModal>
  <NxpModal
    :locked="true"
    :open="Boolean(configCandidates)"
    :title="t('users.take_over_configuration')"
    :aria-label="t('users.take_over_configuration')"
    panel-class="secondary-surface"
    :close-label="t('common.close')"
    @close="flow.close"
  >
    <template v-if="configCandidates">
      <p class="modal-copy">{{ t("users.config.candidates_help") }}</p>
      <div class="first-edit-chooser">
        <NxpButton v-for="candidate in configCandidates.candidates" :key="candidate" class="chooser-card" type="button" @click.stop="flow.chooseCandidate(candidate)">
          <strong class="scroll-text"><span class="scroll-inner">{{ candidate }}</span></strong>
          <span class="muted">{{ t("users.config.candidate_used") }}</span>
        </NxpButton>
      </div>
    </template>
    <template #footer><NxpButton class="ghost" type="button" @click.stop="flow.close">{{ t("common.cancel") }}</NxpButton></template>
  </NxpModal>
  <NxpModal
    :locked="true"
    :open="Boolean(configEdit)"
    :title="t('users.config.edit_progress')"
    :aria-label="t('users.config.edit_progress')"
    panel-class="secondary-surface"
    :close-label="t('common.close')"
    @close="nativeEditAllowed && flow.finish('cancel')"
  >
    <template v-if="configEdit">
      <p class="modal-copy">{{ configEdit.mode === "fresh" ? t("users.config.edit_new_help") : configEdit.mode === "reuse" ? t("users.config.edit_existing_help") : t("users.config.edit_manual_help", { user: configEdit.userName, script: configEdit.scriptName }) }}</p>
    </template>
    <template #footer>
      <NxpButton
        class="primary"
        type="button"
        :busy="finishingAction === 'done'"
        :disabled="finishingAction !== null"
        @click.stop="flow.finish('done')"
      >{{ t("common.complete") }}</NxpButton>
      <NxpButton
        class="ghost"
        type="button"
        :busy="finishingAction === 'cancel'"
        :disabled="finishingAction !== null"
        @click.stop="flow.finish('cancel')"
      >{{ t("common.cancel") }}</NxpButton>
    </template>
  </NxpModal>
</template>
