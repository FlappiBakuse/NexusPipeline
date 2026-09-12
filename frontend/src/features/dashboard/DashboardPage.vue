<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { api, isAbortError } from "../../platform/api";
import { formatList, t } from "../../platform/i18n";
import { renderPluginSlot } from "@bridge/index";
import { disposePluginSlot } from "@bridge/index";
import { setTopbarTitle } from "../../platform/shell";
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpCard from "../../ui/primitives/NxpCard.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpScrollArea from "../../ui/primitives/NxpScrollArea.vue";
import NxpPageHeader from "../../ui/composites/NxpPageHeader.vue";
import SystemActionCard from "./SystemActionCard.vue";

interface RunningRecord {
  targetName?: string;
  kind?: string;
  mode?: string;
  status?: string;
  currentScriptName?: string;
  currentStatus?: string;
  currentAttempt?: number;
  currentMaxAttempts?: number;
  persistenceWarning?: string;
}

interface DashboardStatus {
  version?: string;
  running?: RunningRecord[];
  plugins?: Array<{ configuredEnabled?: boolean; displayName?: string }>;
  systemAction?: { action?: string; deadline?: string; queueName?: string } | null;
}

const status = ref<DashboardStatus>({ running: [], plugins: [] });
const loading = ref(true);
const error = ref("");
const cardsSlot = ref<HTMLElement | null>(null);
const afterRunningSlot = ref<HTMLElement | null>(null);
let timer: ReturnType<typeof setInterval> | null = null;
let requestController: AbortController | null = null;

function tone(value: string | undefined) {
  if (value === "success") return "ok";
  if (value === "running") return "blue";
  if (value === "partial" || value === "cancelled" || value === "skipped") return "warn";
  return "bad";
}

function statusText(value: string | undefined) {
  if (value === "success") return t("common.success");
  if (value === "partial") return t("common.partially_failed");
  if (value === "running") return t("common.running");
  if (value === "cancelled") return t("common.cancelled");
  if (value === "skipped") return t("common.skipped");
  return t("common.failed");
}

function recordType(record: RunningRecord) {
  return t(record.kind === "queue" ? "common.schedule_queues" : "common.script_instance");
}

function recordMode(record: RunningRecord) {
  return t(record.mode === "auto" ? "common.automatic" : "common.manual");
}

function disabledPlugins() {
  return (status.value.plugins || []).filter(plugin => !plugin.configuredEnabled);
}

async function paintSlots() {
  await nextTick();
  if (cardsSlot.value) await renderPluginSlot(cardsSlot.value, "dashboard.cards");
  if (afterRunningSlot.value) await renderPluginSlot(afterRunningSlot.value, "dashboard.after-running");
}

async function load() {
  if (requestController) return;
  const controller = new AbortController();
  requestController = controller;
  try {
    const next = await api("GET", "/api/status", undefined, controller.signal) as DashboardStatus;
    status.value = { running: [], plugins: [], ...next };
    loading.value = false;
    error.value = "";
    const version = document.querySelector<HTMLElement>("#app-version");
    if (version) version.textContent = `${t("common.current_version")} · ${next.version || "0.0.0"}`;
    await paintSlots();
  } catch (reason) {
    if (controller.signal.aborted || isAbortError(reason)) return;
    loading.value = false;
    error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (requestController === controller) requestController = null;
  }
}

onMounted(() => {
  setTopbarTitle(t("dashboard.dashboard"));
  void load();
  timer = setInterval(() => void load(), 3000);
});

onBeforeUnmount(() => {
  if (timer) clearInterval(timer);
  requestController?.abort();
  requestController = null;
  if (cardsSlot.value) void disposePluginSlot(cardsSlot.value);
  if (afterRunningSlot.value) void disposePluginSlot(afterRunningSlot.value);
});
</script>

<template>
  <main id="view" class="view-root" data-testid="main-view">
    <template v-if="loading && !status.version">
      <NxpEmptyState :title="t('common.loading')" />
    </template>
    <template v-else-if="error && !status.version">
      <NxpEmptyState :title="t('dashboard.connection.unavailable')" :description="error" tone="danger" />
    </template>
    <template v-else>
      <NxpPageHeader
        :eyebrow="t('dashboard.run_overview')"
        :title="t('dashboard.dashboard')"
        :description="t('dashboard.overview.help')"
      />
      <div ref="cardsSlot" class="plugin-slot" data-plugin-slot="dashboard.cards" data-plugin-anchor="dashboard.cards" hidden></div>
      <section id="dashboard-state" class="dashboard-state" :class="(status.running || []).length ? 'running' : 'idle'" data-testid="dashboard-state" aria-live="polite">
        <div class="dashboard-state-copy"><div class="state-label">{{ (status.running || []).length ? t("common.running") : t("dashboard.system_idle") }}</div><h3>{{ (status.running || []).length ? t("dashboard.task_in_progress") : t("dashboard.everything_is_ready") }}</h3><p>{{ (status.running || []).length ? t("dashboard.running.summary", { count: (status.running || []).length }) : t("dashboard.running.empty_help") }}</p></div>
      </section>
      <div id="system-action-area"><SystemActionCard v-if="status.systemAction && status.systemAction.action !== 'exit'" :action="status.systemAction" @cancelled="load" /></div>
      <NxpCard class="content-section list-surface" data-testid="running-panel">
        <div class="section-heading"><h3>{{ t("common.running") }}</h3><span class="muted">{{ t("dashboard.active_tasks.count", { count: (status.running || []).length }) }}</span></div>
        <NxpEmptyState v-if="!(status.running || []).length" :title="t('dashboard.idle')" :description="t('dashboard.running.empty')" link-href="#/dispatch" :link-label="t('dashboard.go_to_dispatch')" />
        <template v-else>
          <NxpScrollArea class="table-scroll running-table" direction="horizontal" :aria-label="t('dashboard.running.table')"><table class="data-table"><thead><tr><th scope="col">{{ t("common.task") }}</th><th scope="col">{{ t("dashboard.type") }}</th><th scope="col">{{ t("dashboard.mode") }}</th><th scope="col">{{ t("dashboard.progress") }}</th><th scope="col">{{ t("common.status") }}</th></tr></thead><tbody><tr v-for="record in status.running" :key="record.targetName"><td><strong>{{ record.targetName }}</strong></td><td>{{ recordType(record) }}</td><td>{{ recordMode(record) }}</td><td>{{ record.currentScriptName || "-" }} {{ record.currentStatus || "" }}<br><span class="muted">{{ t("dashboard.running.attempt", { attempt: record.currentAttempt, max: record.currentMaxAttempts }) }}</span></td><td><NxpBadge :tone="tone(record.status)">{{ statusText(record.status) }}</NxpBadge></td></tr></tbody></table></NxpScrollArea>
          <div class="running-records"><article v-for="record in status.running" :key="`mobile-${record.targetName}`" class="running-record"><div class="running-record-head"><strong>{{ record.targetName }}</strong><NxpBadge :tone="tone(record.status)">{{ statusText(record.status) }}</NxpBadge></div><div class="running-record-meta"><span>{{ recordType(record) }}</span><span>{{ recordMode(record) }}</span></div><div class="running-record-progress">{{ record.currentScriptName || "-" }} {{ record.currentStatus || "" }}<br><span class="muted">{{ t("dashboard.running.attempt", { attempt: record.currentAttempt, max: record.currentMaxAttempts }) }}</span><br v-if="record.persistenceWarning"><span v-if="record.persistenceWarning" class="badge warn">{{ t("dashboard.persistence_warning", { label: t("common.history.persistence_warning"), warning: record.persistenceWarning }) }}</span></div></article></div>
        </template>
      </NxpCard>
      <div ref="afterRunningSlot" class="plugin-slot" data-plugin-slot="dashboard.after-running" data-plugin-anchor="dashboard.after-running" hidden></div>
      <section v-if="disabledPlugins().length" class="dashboard-system-note" data-testid="plugin-health"><p>{{ t("dashboard.plugins.disabled_summary", { count: disabledPlugins().length, plugins: formatList(disabledPlugins().map(plugin => plugin.displayName || "")) }) }}</p><a class="back-link" href="#/plugins">{{ t("dashboard.view_plugins") }}</a></section>
    </template>
  </main>
</template>
