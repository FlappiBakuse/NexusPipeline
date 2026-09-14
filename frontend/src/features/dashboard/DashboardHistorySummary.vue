<script setup lang="ts">
import { computed } from "vue";
import { formatNumber, t } from "../../platform/i18n";
import { formatDurationMs, formatHistoryDate } from "../history/utils/historyFormat";
import type { HistoryStatus, HistorySummary } from "../history/utils/historyTypes";

const props = defineProps<{
  summary: HistorySummary | null;
  loading: boolean;
  error: string;
  from: string;
  to: string;
}>();

const statusDefinitions: Array<{ value: HistoryStatus; label: string; optional?: boolean }> = [
  { value: "success", label: "common.success" },
  { value: "partial", label: "common.partially_failed" },
  { value: "failed", label: "common.failed" },
  { value: "cancelled", label: "common.cancelled", optional: true },
  { value: "skipped", label: "common.skipped", optional: true },
];

function count(status: HistoryStatus) {
  return Number(props.summary?.statusCounts?.[status] || 0);
}

const visibleStatuses = computed(() => statusDefinitions.filter(item => !item.optional || count(item.value) > 0));

function summaryRate() {
  return props.summary?.successRate == null
    ? "-"
    : `${formatNumber(props.summary.successRate, { maximumFractionDigits: 1 })}%`;
}
</script>

<template>
  <section class="dashboard-history-summary" data-testid="dashboard-history-summary">
    <article class="dashboard-history-card" data-testid="dashboard-history-summary-runs">
      <div class="dashboard-history-card-head">
        <div>
          <span class="eyebrow">{{ t("dashboard.history.eyebrow") }}</span>
          <h2>{{ t("dashboard.history.recent_runs") }}</h2>
        </div>
        <span class="muted dashboard-history-range">{{ formatHistoryDate(from) }} {{ t("common.to") }} {{ formatHistoryDate(to) }}</span>
      </div>
      <div v-if="loading && !summary" class="muted dashboard-history-loading" role="status">{{ t("common.loading") }}</div>
      <p v-else-if="error" class="dashboard-history-error" role="status">{{ t("dashboard.history.unavailable") }}</p>
      <template v-else-if="summary">
        <div class="dashboard-history-total" data-testid="dashboard-history-summary-total">
          <span class="k">{{ t("history.summary.total") }}</span>
          <strong class="v">{{ formatNumber(summary.totalCount) }}</strong>
        </div>
        <div class="dashboard-history-statuses" data-testid="dashboard-history-summary-statuses">
          <span v-for="item in visibleStatuses" :key="item.value" class="dashboard-history-status" :data-status="item.value">
            <span>{{ t(item.label) }}</span><strong>{{ formatNumber(count(item.value)) }}</strong>
          </span>
        </div>
      </template>
      <p v-else class="muted dashboard-history-loading">{{ t("dashboard.history.unavailable") }}</p>
    </article>

    <article class="dashboard-history-card" data-testid="dashboard-history-summary-performance">
      <div class="dashboard-history-card-head">
        <div>
          <span class="eyebrow">{{ t("dashboard.history.eyebrow") }}</span>
          <h2>{{ t("dashboard.history.performance") }}</h2>
        </div>
        <span class="muted dashboard-history-range">{{ formatHistoryDate(from) }} {{ t("common.to") }} {{ formatHistoryDate(to) }}</span>
      </div>
      <div v-if="loading && !summary" class="muted dashboard-history-loading" role="status">{{ t("common.loading") }}</div>
      <p v-else-if="error" class="dashboard-history-error" role="status">{{ t("dashboard.history.unavailable") }}</p>
      <div v-else-if="summary" class="dashboard-history-metrics">
        <div class="dashboard-history-metric">
          <span>{{ t("history.summary.success_rate") }}</span><strong>{{ summaryRate() }}</strong>
        </div>
        <div class="dashboard-history-metric">
          <span>{{ t("history.summary.average_duration") }}</span><strong>{{ formatDurationMs(summary.averageDurationMs) }}</strong>
        </div>
        <div class="dashboard-history-metric">
          <span>{{ t("history.summary.total_duration") }}</span><strong>{{ formatDurationMs(summary.totalDurationMs) }}</strong>
        </div>
      </div>
      <p v-else class="muted dashboard-history-loading">{{ t("dashboard.history.unavailable") }}</p>
    </article>
  </section>
</template>
