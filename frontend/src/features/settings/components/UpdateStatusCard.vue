<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { api, isAbortError } from "../../../platform/api";
import { t } from "../../../platform/i18n";
import { toast } from "../../../platform/toast";
import { updateStatusView as updateStatusViewFor } from "../utils/updateStatusView";
import type { UpdateStatus } from "../utils/settingsTypes";

/** 更新状态卡片：独立承担更新状态轮询、检查/下载/取消/应用事务与 ready 常驻备份提醒。 */

const updateStatus = ref<UpdateStatus | null>(null);
let disposed = false;
let updateTimer: ReturnType<typeof setTimeout> | null = null;

const completionActionText = computed(() => {
  const state = updateStatus.value?.state || "idle";
  if (state === "checking") return t("settings.update.checking");
  if (state === "downloading") return t("settings.update.downloading");
  if (state === "ready") return t("settings.update.ready_confirm");
  if (state === "applying") return t("settings.update.applying");
  if (updateStatus.value?.available)
    return t("settings.update.version", { version: updateStatus.value.latest || "" });
  return updateStatus.value?.checked
    ? t("settings.update.latest")
    : t("settings.update.not_checked");
});
const updateStatusView = computed(() => updateStatusViewFor(updateStatus.value?.state));

function errorText(reason: unknown) {
  return reason instanceof Error ? reason.message : String(reason);
}

async function loadUpdateStatus() {
  try {
    updateStatus.value = (await api("GET", "/api/update/status")) as UpdateStatus;
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
    updateStatus.value = (await api("POST", "/api/update/check")) as UpdateStatus;
    toast(
      updateStatus.value?.available
        ? t("settings.update.version_available", { version: updateStatus.value.latest || "" })
        : t("common.already_up_to_date"),
      "info",
    );
    scheduleUpdatePoll();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}
async function downloadUpdate() {
  try {
    await api("POST", "/api/update/download");
    await loadUpdateStatus();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}
async function cancelUpdate() {
  try {
    await api("POST", "/api/update/cancel");
    toast(t("settings.download_cancelled"));
    await loadUpdateStatus();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}
async function applyUpdate(defer: boolean) {
  if (
    !window.confirm(
      t("settings.update.backup_confirm", {
        action: defer ? t("settings.update.apply_next_start") : t("settings.update.apply_now"),
        version: updateStatus.value?.latest ? ` v${updateStatus.value.latest}` : "",
      }),
    )
  )
    return;
  try {
    const result = (await api("POST", "/api/update/apply", { defer })) as { error?: string; deferred?: boolean } | null;
    if (result?.error) {
      toast(result.error, "error");
      return;
    }
    toast(result?.deferred ? t("settings.update.queued_service_start") : t("settings.update_apply_started"));
    await loadUpdateStatus();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

onMounted(() => {
  disposed = false;
  void loadUpdateStatus();
});
onBeforeUnmount(() => {
  disposed = true;
  if (updateTimer) clearTimeout(updateTimer);
});

defineExpose({ reload: loadUpdateStatus });
</script>

<template>
  <div id="update-status-box" class="update-status" data-testid="update-status">
    <div class="detail">
      <div class="kv">
        <span class="k">{{ t("common.current_version") }}</span
        ><span>v{{ updateStatus?.current || "—" }}</span>
      </div>
      <div class="kv">
        <span class="k">{{ t("settings.update_channel") }}</span
        ><span>{{ updateStatus?.channel === "stable" ? t("settings.stable") : t("settings.pre_release") }}</span>
      </div>
    </div>
    <p class="muted update-state-copy">{{ completionActionText }}</p>
    <p v-if="updateStatusView.showBackupWarning" class="callout callout-warning update-backup-warning" data-testid="update-backup-warning">
      {{ t("settings.update.backup_help") }}
    </p>
    <div v-if="updateStatus?.notes" class="update-notes">{{ String(updateStatus.notes).slice(0, 300) }}</div>
    <div
      v-if="updateStatus?.state === 'downloading' && typeof updateStatus.progress === 'number'"
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
        :disabled="updateStatus?.state === 'checking' || updateStatus?.state === 'downloading' || updateStatus?.state === 'ready'"
        @click="checkUpdate"
      >
        {{ t("common.check_for_updates") }}</button
      ><button v-if="updateStatus?.state === 'downloading'" class="ghost" type="button" @click="cancelUpdate">
        {{ t("common.cancel_download") }}</button
      ><button v-else-if="updateStatus?.state === 'idle' && updateStatus?.available" class="ghost" type="button" @click="downloadUpdate">
        {{ t("common.download_update") }}</button
      ><button v-if="updateStatus?.state === 'ready'" class="primary" type="button" @click="applyUpdate(false)">
        {{ t("common.update_now") }}</button
      ><button v-if="updateStatus?.state === 'ready'" class="ghost" type="button" @click="applyUpdate(true)">
        {{ t("common.update_on_next_startup") }}
      </button>
    </div>
  </div>
</template>
