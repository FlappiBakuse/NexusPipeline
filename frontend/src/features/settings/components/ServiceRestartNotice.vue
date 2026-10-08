<script setup lang="ts">
import { computed, ref } from "vue";
import { useShellStore } from "../../../stores/shell";
import { t } from "../../../platform/i18n";
import { toast } from "../../../platform/toast";
import { beginServiceRecovery, resumeServiceRecovery } from "../../../platform/service-recovery";
import NxpConfirmDialog from "../../../ui/composites/NxpConfirmDialog.vue";
import NxpButton from "../../../ui/primitives/NxpButton.vue";

const shell = useShellStore();
const props = defineProps<{ beforeRestart?: () => Promise<void> }>();
const preparing = ref(false);
const confirmationOpen = ref(false);

const message = computed(() => {
  if (shell.restarting) return t("settings.service_restarting");
  return shell.restartError || t("settings.service.restart_notice");
});

/** 重启期间只显示进行中的提示：页面在确认新实例接管后自动刷新。 */
const actionVisible = computed(() => !shell.restarting);

function requestRestart() {
  if (shell.restarting) return;
  if (shell.recoveryPhase === "timeout" || shell.recoveryPhase === "failed") {
    resumeServiceRecovery();
    return;
  }
  confirmationOpen.value = true;
}

async function confirmRestart() {
  if (shell.restarting || preparing.value) return;
  confirmationOpen.value = false;
  preparing.value = true;
  try {
    await props.beforeRestart?.();
    await beginServiceRecovery();
  } catch (reason) {
    toast(reason instanceof Error ? reason.message : String(reason), "error");
  } finally {
    preparing.value = false;
  }
}
</script>

<template>
  <section
    v-if="shell.restartRequired"
    id="service-restart-notice"
    class="callout callout-warning callout-actions"
    role="status"
    aria-live="polite"
    data-testid="service-restart-notice"
  >
    <p>{{ message }}</p>
    <NxpButton
      v-if="actionVisible"
      class="primary"
      type="button"
      data-testid="restart-service"
      :disabled="preparing"
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
