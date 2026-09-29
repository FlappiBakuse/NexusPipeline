<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useShellStore } from "../../../stores/shell";
import { t } from "../../../platform/i18n";
import { api } from "../../../platform/api";
import { beginServiceRecovery, resumeServiceRecovery } from "../../../platform/service-recovery";
import NxpConfirmDialog from "../../../ui/composites/NxpConfirmDialog.vue";
import NxpButton from "../../../ui/primitives/NxpButton.vue";

const shell = useShellStore();
const lightweight = ref(false);
const confirmationOpen = ref(false);

/** 轻量模式不启动 Web 服务，此时只提示手动重启，不显示不可执行的按钮。 */
async function loadServiceMode() {
  try {
    const status = await api<{ lightweightMode?: boolean }>("GET", "/api/status");
    lightweight.value = status?.lightweightMode === true;
  } catch {
    lightweight.value = false;
  }
}

watch(() => shell.restartRequired, required => {
  if (required) void loadServiceMode();
});

const message = computed(() => {
  if (shell.restarting) return t("settings.service_restarting");
  return shell.restartError || t("settings.service.restart_notice");
});

/** 重启期间只显示进行中的提示：页面在确认新实例接管后自动刷新。 */
const actionVisible = computed(() => !shell.restarting && !lightweight.value);

function requestRestart() {
  if (shell.restarting) return;
  if (shell.recoveryPhase === "timeout" || shell.recoveryPhase === "failed") {
    resumeServiceRecovery();
    return;
  }
  confirmationOpen.value = true;
}

function confirmRestart() {
  if (shell.restarting) return;
  confirmationOpen.value = false;
  void beginServiceRecovery();
}
</script>

<template>
  <section
    v-if="shell.restartRequired"
    id="service-restart-notice"
    class="dashboard-system-note"
    role="status"
    aria-live="polite"
    data-testid="service-restart-notice"
  >
    <p>{{ message }}</p>
    <span v-if="lightweight" class="muted">{{ t("settings.service.lightweight_restart") }}</span>
    <NxpButton
      v-else-if="actionVisible"
      class="primary"
      type="button"
      data-testid="restart-service"
      @click="requestRestart"
    >
      {{ t("settings.restart_service") }}
    </NxpButton>
  </section>
  <NxpConfirmDialog
    :open="confirmationOpen"
    :title="t('settings.service_restart')"
    :message="t('settings.restart_warning')"
    :confirm-label="t('settings.restart_service')"
    :cancel-label="t('common.cancel')"
    confirm-tone="danger"
    @confirm="confirmRestart"
    @cancel="confirmationOpen = false"
    @close="confirmationOpen = false"
  />
</template>
