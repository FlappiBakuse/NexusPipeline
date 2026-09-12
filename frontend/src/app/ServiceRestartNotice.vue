<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useShellStore } from "../stores/shell";
import { t } from "../platform/i18n";
import { api } from "../platform/api";
import { restartService } from "../platform/service-restart";

const shell = useShellStore();
const lightweight = ref(false);
const showRetry = ref(false);

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

async function triggerRestart() {
  if (shell.restarting) return;
  if (!window.confirm(t("settings.restart_warning"))) return;
  showRetry.value = false;
  shell.beginRestart();
  const outcome = await restartService({ timeoutMs: 40_000 });
  if (outcome === "ready") return;
  showRetry.value = true;
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
      v-else
      class="primary"
      type="button"
      data-testid="restart-service"
      :disabled="shell.restarting"
      @click="triggerRestart"
    >
      {{ shell.restarting
        ? t("common.restart")
        : showRetry ? t("settings.service.restart_retry") : t("settings.restart_service") }}
    </button>
  </section>
</template>
