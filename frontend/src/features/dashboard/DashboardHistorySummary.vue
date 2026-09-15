<script setup lang="ts">
import { computed } from "vue";
import { formatNumber, t } from "../../platform/i18n";
import { formatDurationMs, formatHistoryDate, formatHistoryShortDate } from "../history/utils/historyFormat";
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

const DONUT_RADIUS = 44;
const DONUT_CIRCUMFERENCE = 2 * Math.PI * DONUT_RADIUS;

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

const donutSegments = computed(() => {
  if (distributionTotal.value <= 0) return [];
  let offset = 0;
  return statusDefinitions.map((item) => {
    const length = (count(item.value) / distributionTotal.value) * DONUT_CIRCUMFERENCE;
    const segment = {
      ...item,
      dasharray: `${length} ${Math.max(0, DONUT_CIRCUMFERENCE - length)}`,
      dashoffset: `${-offset}`,
    };
    offset += length;
    return segment;
  });
});

const distributionAria = computed(() => {
  const fallback = `${t("dashboard.history.distribution_aria")} · ${formatNumber(distributionTotal.value)}`;
  return t(
    "dashboard.history.distribution_detail",
    { total: formatNumber(distributionTotal.value) },
    fallback,
  );
});

function statusAria(item: { value: HistoryStatus; label: string }) {
  return `${t(item.label)} · ${formatNumber(count(item.value))} · ${formatNumber(statusPercentage(item.value), { maximumFractionDigits: 1 })}%`;
}

const dailyData = computed(() => (Array.isArray(props.summary?.daily) ? props.summary!.daily : [])
  .slice(-7)
  .map((item) => {
    const total = safeCount(item.totalCount);
    const success = Math.min(total, safeCount(item.statusCounts?.success));
    return {
      date: item.date,
      label: formatHistoryShortDate(item.date),
      total,
      success,
      other: Math.max(0, total - success),
    };
  }));

const maxDailyTotal = computed(() => Math.max(1, ...dailyData.value.map(item => item.total)));

function dailyWidth(value: number) {
  return `${value > 0 ? (value / maxDailyTotal.value) * 100 : 0}%`;
}

function dailySuccessWidth(point: { total: number; success: number }) {
  return point.total > 0 ? `${(point.success / point.total) * 100}%` : "0%";
}

function dailyTooltip(point: { label: string; total: number; success: number; other: number }) {
  return `${point.label} · ${t("history.summary.total", {}, "总数")}: ${formatNumber(point.total)} · ${t("common.success", {}, "成功")}: ${formatNumber(point.success)} · ${t("dashboard.history.trend_other", {}, "其他")}: ${formatNumber(point.other)}`;
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
          <div class="dashboard-history-distribution">
            <div class="dashboard-history-donut" role="img" :aria-label="distributionAria">
              <svg viewBox="0 0 120 120" aria-hidden="true">
                <circle class="dashboard-history-donut-track" :cx="60" :cy="60" :r="DONUT_RADIUS" />
                <circle
                  v-for="item in donutSegments"
                  :key="`donut-${item.value}`"
                  class="dashboard-history-donut-segment"
                  :class="`status-${item.value}`"
                  :cx="60"
                  :cy="60"
                  :r="DONUT_RADIUS"
                  :style="{ strokeDasharray: item.dasharray, strokeDashoffset: item.dashoffset }"
                />
              </svg>
              <div class="dashboard-history-donut-total" aria-hidden="true">
                <strong>{{ formatNumber(distributionTotal) }}</strong>
                <span>{{ t("history.summary.total") }}</span>
              </div>
            </div>
            <div class="dashboard-history-status-legend" role="list" :aria-label="t('dashboard.history.distribution')">
              <div
                v-for="item in statusDefinitions"
                :key="item.value"
                class="dashboard-history-status"
                :class="{ 'is-muted': count(item.value) === 0 }"
                :data-status="item.value"
                role="listitem"
                tabindex="0"
                :aria-label="statusAria(item)"
              >
                <span class="dashboard-history-status-dot" :class="`status-${item.value}`" aria-hidden="true" />
                <span class="dashboard-history-status-label">{{ t(item.label) }}</span>
                <strong>{{ formatNumber(count(item.value)) }}</strong>
                <small>{{ formatNumber(statusPercentage(item.value), { maximumFractionDigits: 1 }) }}%</small>
              </div>
            </div>
          </div>
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
            <span class="dashboard-history-trend-legend" :aria-label="t('dashboard.history.trend_legend')">
              <span class="dashboard-history-legend-chip"><i class="dashboard-history-legend-dot dashboard-history-trend-success" aria-hidden="true" />{{ t("common.success") }}</span>
              <span class="dashboard-history-legend-chip"><i class="dashboard-history-legend-dot dashboard-history-trend-other" aria-hidden="true" />{{ t("dashboard.history.trend_other") }}</span>
            </span>
          </div>
          <div class="dashboard-history-trend" data-testid="dashboard-history-trend" role="list" :aria-label="t('dashboard.history.trend_aria')">
            <div v-if="dailyData.length" class="dashboard-history-trend-rows">
              <button
                v-for="point in dailyData"
                :key="point.date"
                class="dashboard-history-trend-day"
                type="button"
                role="listitem"
                :data-tooltip="dailyTooltip(point)"
                :aria-label="dailyTooltip(point)"
              >
                <span class="dashboard-history-trend-label">{{ point.label }}</span>
                <span class="dashboard-history-trend-track" aria-hidden="true">
                  <span class="dashboard-history-trend-fill" :style="{ width: dailyWidth(point.total) }">
                    <span class="dashboard-history-trend-segment dashboard-history-trend-success" :style="{ width: dailySuccessWidth(point) }" />
                    <span class="dashboard-history-trend-segment dashboard-history-trend-other" :style="{ width: point.total > 0 ? `${(point.other / point.total) * 100}%` : '0%' }" />
                  </span>
                </span>
                <strong class="dashboard-history-trend-total">{{ formatNumber(point.total) }}</strong>
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
