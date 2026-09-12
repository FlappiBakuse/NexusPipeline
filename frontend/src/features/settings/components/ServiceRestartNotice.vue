<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useShellStore } from "../../../stores/shell";
import { t } from "../../../platform/i18n";
import { api } from "../../../platform/api";
import { restartService, type ServiceRestartHandoff } from "../../../platform/service-restart";

const shell = useShellStore();
const lightweight = ref(false);
/** 已提交的重启交接信息：失败后重试时复用，避免对已经退出的旧实例再次提交重启请求。 */
const pendingHandoff = ref<ServiceRestartHandoff | null>(null);

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

async function triggerRestart() {
  if (shell.restarting) return;
  if (!window.confirm(t("settings.restart_warning"))) return;
  shell.beginRestart();
  const outcome = await restartService({
    timeoutMs: 40_000,
    handoff: pendingHandoff.value || undefined,
    onHandoff: handoff => {
      pendingHandoff.value = handoff;
    },
  });
  if (outcome === "ready") return;
  shell.failRestart(outcome === "timeout"
    ? t("settings.service.restart_timeout")
    : t("settings.service.restart_failed"));
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
    <button
      v-else-if="actionVisible"
      class="primary"
      type="button"
      data-testid="restart-service"
      @click="triggerRestart"
    >
      {{ t("settings.restart_service") }}
    </button>
  </section>
</template>
