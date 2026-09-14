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

const statusDefinitions: Array<{ value: HistoryStatus; label: string }> = [
  { value: "success", label: "common.success" },
  { value: "partial", label: "common.partially_failed" },
  { value: "failed", label: "common.failed" },
  { value: "cancelled", label: "common.cancelled" },
  { value: "skipped", label: "common.skipped" },
];

function safeCount(value: unknown) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? Math.floor(parsed) : 0;
}

function count(status: HistoryStatus) {
  return safeCount(props.summary?.statusCounts?.[status]);
}

const distributionTotal = computed(() => Math.max(
  safeCount(props.summary?.totalCount),
  statusDefinitions.reduce((total, item) => total + count(item.value), 0),
));

function statusPercentage(status: HistoryStatus) {
  const total = distributionTotal.value;
  return total > 0 ? (count(status) / total) * 100 : 0;
}

const dailyData = computed(() => (Array.isArray(props.summary?.daily) ? props.summary!.daily : [])
  .slice(-7)
  .map((item) => {
    const total = safeCount(item.totalCount);
    const success = Math.min(total, countFromDaily(item.statusCounts?.success));
    return { date: item.date, label: formatHistoryDate(item.date), total, success, other: Math.max(0, total - success) };
  }));

function countFromDaily(value: unknown) {
  return safeCount(value);
}

const maxDailyTotal = computed(() => Math.max(1, ...dailyData.value.map(item => item.total)));

function dailyHeight(value: number) {
  return value > 0 ? `${Math.max(8, (value / maxDailyTotal.value) * 100)}%` : "0%";
}

function summaryRate() {
  return props.summary?.successRate == null || !Number.isFinite(Number(props.summary.successRate))
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
        <div class="dashboard-history-statuses" data-testid="dashboard-history-summary-statuses">
          <div class="dashboard-history-subheading">
            <span class="k">{{ t("dashboard.history.distribution") }}</span>
            <span class="muted">{{ t("history.summary.total") }} {{ formatNumber(distributionTotal) }}</span>
          </div>
          <div class="dashboard-history-status-bar" role="img" :aria-label="t('dashboard.history.distribution_aria')">
            <span
              v-for="item in statusDefinitions"
              :key="`bar-${item.value}`"
              class="dashboard-history-status-bar-segment"
              :class="`status-${item.value}`"
              :style="{ width: `${statusPercentage(item.value)}%` }"
            />
          </div>
          <span v-for="item in statusDefinitions" :key="item.value" class="dashboard-history-status" :data-status="item.value">
            <span>{{ t(item.label) }}</span><strong>{{ formatNumber(count(item.value)) }}</strong><small>{{ formatNumber(statusPercentage(item.value), { maximumFractionDigits: 1 }) }}%</small>
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
        <div class="dashboard-history-trend-block">
          <div class="dashboard-history-subheading">
            <span class="k">{{ t("dashboard.history.trend") }}</span>
            <span class="muted">{{ t("dashboard.history.trend_legend") }}</span>
          </div>
          <div class="dashboard-history-trend" data-testid="dashboard-history-trend" role="img" :aria-label="t('dashboard.history.trend_aria')">
            <div v-if="dailyData.length" class="dashboard-history-trend-bars" aria-hidden="true">
              <button
                v-for="point in dailyData"
                :key="point.date"
                class="dashboard-history-trend-day"
                type="button"
                :data-tooltip="`${point.label} · ${t('history.summary.total', {}, '总数')}: ${formatNumber(point.total)} · ${t('common.success', {}, '成功')}: ${formatNumber(point.success)} · ${t('dashboard.history.trend_other', {}, '其他')}: ${formatNumber(point.other)}`"
                :aria-label="`${point.label} · ${t('history.summary.total', {}, '总数')}: ${formatNumber(point.total)} · ${t('common.success', {}, '成功')}: ${formatNumber(point.success)} · ${t('dashboard.history.trend_other', {}, '其他')}: ${formatNumber(point.other)}`"
              >
                <div class="dashboard-history-trend-bar" :style="{ height: dailyHeight(point.total) }">
                  <span class="dashboard-history-trend-segment dashboard-history-trend-success" :style="{ flexGrow: point.success }" />
                  <span class="dashboard-history-trend-segment dashboard-history-trend-other" :style="{ flexGrow: point.other }" />
                </div>
                <span class="dashboard-history-trend-label">{{ point.label }}</span>
              </button>
            </div>
            <span v-else class="muted">{{ t("dashboard.history.trend_empty") }}</span>
          </div>
          <ul class="sr-only">
            <li v-for="point in dailyData" :key="`sr-${point.date}`">{{ t("dashboard.history.day_summary", { date: point.label, success: point.success, other: point.other, total: point.total }) }}</li>
          </ul>
        </div>
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
