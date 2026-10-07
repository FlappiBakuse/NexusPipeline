<script setup lang="ts">
import { computed, ref } from "vue";
import NxpOverflowText from "../../ui/primitives/NxpOverflowText.vue";
import { formatNumber, t } from "../../platform/i18n";
import { formatDurationMs, formatHistoryDate, formatHistoryShortDate } from "../history/utils/historyFormat";
import type { HistorySummary } from "../history/utils/historyTypes";
const props = defineProps<{ summary: HistorySummary | null; loading: boolean; error: string; from: string; to: string }>();
const selected = ref<{ chart: string; index: number } | null>(null);
const width = 560, height = 260, left = 58, right = 58, top = 24, bottom = 40;
function positive(value: unknown) { const number = Number(value); return Number.isFinite(number) ? Math.max(0, number) : 0; }
const days = computed(() => {
  const entries = new Map((props.summary?.daily || []).map(item => [item.date, item]));
  const end = new Date(`${props.to}T12:00:00`);
  return Array.from({ length: 7 }, (_, index) => {
    const day = new Date(end); day.setDate(day.getDate() - 6 + index);
    const date = `${day.getFullYear()}-${String(day.getMonth() + 1).padStart(2, "0")}-${String(day.getDate()).padStart(2, "0")}`;
    const item = entries.get(date), total = Math.floor(positive(item?.totalCount));
    const success = Math.min(total, Math.floor(positive(item?.statusCounts?.success)));
    return { date, label: formatHistoryShortDate(date), duration: positive(item?.totalDurationMs), total, success, abnormal: total - success };
  });
});
const durationUnit = computed(() => {
  const maximum = Math.max(...days.value.map(day => day.duration));
  return maximum >= 3600000 ? { divisor: 3600000, label: t("dashboard.history.hours") }
    : maximum >= 60000 ? { divisor: 60000, label: t("dashboard.history.minutes") }
      : { divisor: 1000, label: t("dashboard.history.seconds") };
});
function ceiling(value: number, integer: boolean) {
  if (!value) return integer ? 4 : 1;
  const raw = value / 4, magnitude = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 5, 10].find(candidate => candidate * magnitude >= raw)! * magnitude;
  return Math.max(integer ? 1 : .25, integer ? Math.ceil(step) : step) * 4;
}
const charts = computed(() => [
  { id: "duration", title: t("dashboard.history.daily_duration"), unit: durationUnit.value.label, max: ceiling(Math.max(...days.value.map(day => day.duration / durationUnit.value.divisor)), false), series: [{ id: "duration", label: t("history.summary.total_duration"), values: days.value.map(day => day.duration / durationUnit.value.divisor), shape: "circle" }] },
  { id: "counts", title: t("dashboard.history.daily_counts"), unit: t("dashboard.history.count_unit"), max: ceiling(Math.max(...days.value.map(day => day.total)), true), series: [
    { id: "total", label: t("dashboard.history.total_runs"), values: days.value.map(day => day.total), shape: "circle" },
    { id: "success", label: t("dashboard.history.success_runs"), values: days.value.map(day => day.success), shape: "square" },
    { id: "abnormal", label: t("dashboard.history.abnormal_runs"), values: days.value.map(day => day.abnormal), shape: "diamond" },
  ] },
]);
function x(index: number) { return left + index * (width - left - right) / 6; }
function y(value: number, max: number) { return height - bottom - value / max * (height - top - bottom); }
function points(values: number[], max: number) { return values.map((value, index) => `${x(index)},${y(value, max)}`).join(" "); }
function detail(chart: string, index: number) {
  const day = days.value[index];
  return chart === "duration" ? `${formatHistoryDate(day.date)} · ${formatDurationMs(day.duration)} (${t("history.duration.ms", { value: formatNumber(day.duration, { maximumFractionDigits: 0 }) })})`
    : `${formatHistoryDate(day.date)} · ${t("dashboard.history.total_runs")}: ${formatNumber(day.total)} · ${t("dashboard.history.success_runs")}: ${formatNumber(day.success)} · ${t("dashboard.history.abnormal_runs")}: ${formatNumber(day.abnormal)}`;
}
function overview(chart: string) {
  const totals = days.value.reduce((sum, day) => ({ duration: sum.duration + day.duration, total: sum.total + day.total, success: sum.success + day.success, abnormal: sum.abnormal + day.abnormal }), { duration: 0, total: 0, success: 0, abnormal: 0 });
  return chart === "duration" ? t("dashboard.history.seven_day_duration", { duration: formatDurationMs(totals.duration) })
    : t("dashboard.history.seven_day_counts", { total: formatNumber(totals.total), success: formatNumber(totals.success), abnormal: formatNumber(totals.abnormal) });
}
function move(event: KeyboardEvent, chart: string, index: number) {
  if (!["ArrowLeft", "ArrowRight", "Home", "End", "Enter", " "].includes(event.key)) return;
  event.preventDefault();
  const next = event.key === "Home" ? 0 : event.key === "End" ? 6 : event.key === "ArrowLeft" ? Math.max(0, index - 1) : event.key === "ArrowRight" ? Math.min(6, index + 1) : index;
  (event.currentTarget as SVGElement).ownerSVGElement?.querySelector<SVGElement>(`[data-chart-day="${next}"]`)?.focus();
  selected.value = { chart, index: next };
}
</script>

<template>
  <section class="dashboard-history-summary" data-testid="dashboard-history-summary">
    <article v-for="chart in charts" :key="chart.id" class="dashboard-history-card">
      <div class="dashboard-history-card-head"><h2>{{ chart.title }}</h2><span class="muted dashboard-history-range">{{ formatHistoryDate(from) }} {{ t("common.to") }} {{ formatHistoryDate(to) }}</span></div>
      <div v-if="loading && !summary" class="dashboard-history-loading muted" role="status">{{ t("common.loading") }}</div>
      <p v-else-if="error || !summary" class="dashboard-history-error" role="status">{{ t("dashboard.history.unavailable") }}</p>
      <template v-else>
        <div class="dashboard-line-legend"><span v-for="series in chart.series" :key="series.id" :class="`series-${series.id}`"><i :class="`marker-${series.shape}`" aria-hidden="true" />{{ series.label }}</span></div>
        <svg class="dashboard-line-chart" :viewBox="`0 0 ${width} ${height}`" role="group" :aria-label="chart.title" @mouseleave="selected = null">
          <text class="chart-unit" :x="left" y="13">{{ chart.unit }}</text>
          <g v-for="tick in [0, 1, 2, 3, 4]" :key="tick" aria-hidden="true"><line class="chart-grid" :x1="left" :x2="width - right" :y1="y(chart.max * tick / 4, chart.max)" :y2="y(chart.max * tick / 4, chart.max)" /><text class="chart-tick" :x="left - 10" :y="y(chart.max * tick / 4, chart.max) + 4" text-anchor="end">{{ formatNumber(chart.max * tick / 4, { maximumFractionDigits: 2 }) }}</text></g>
          <g v-for="series in chart.series" :key="series.id" :class="`series-${series.id}`" aria-hidden="true">
            <polyline class="chart-line" :points="points(series.values, chart.max)" :class="`line-${series.shape}`" />
            <g v-for="(value, index) in series.values" :key="index" :transform="`translate(${x(index)}, ${y(value, chart.max)})`"><circle v-if="series.shape === 'circle'" class="chart-point" r="4" /><rect v-else-if="series.shape === 'square'" class="chart-point" x="-3" y="-3" width="6" height="6" /><path v-else class="chart-point" d="M 0 -5 L 5 0 L 0 5 L -5 0 Z" /></g>
          </g>
          <g v-for="(day, index) in days" :key="day.date"><text class="chart-tick" :x="x(index)" :y="height - 14" text-anchor="middle" aria-hidden="true">{{ day.label }}</text><rect class="chart-day-target" :class="{ 'is-selected': selected?.chart === chart.id && selected.index === index }" :x="x(index) - 40" :y="top - 6" width="80" :height="height - top - bottom + 12" tabindex="0" role="button" :data-chart-day="index" :aria-label="detail(chart.id, index)" @pointerenter="selected = { chart: chart.id, index }" @focus="selected = { chart: chart.id, index }" @blur="selected = null" @click="selected = { chart: chart.id, index }" @keydown="move($event, chart.id, index)"><title>{{ detail(chart.id, index) }}</title></rect></g>
        </svg>
        <p class="dashboard-chart-detail" aria-live="polite"><NxpOverflowText>{{ selected?.chart === chart.id ? detail(chart.id, selected.index) : overview(chart.id) }}</NxpOverflowText></p>
      </template>
    </article>
  </section>
</template>

<style>
.dashboard-history-summary { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 18px; }
.dashboard-history-card { display: flex; flex-direction: column; min-width: 0; padding: 20px; border: 1px solid var(--content-card-border); border-radius: var(--radius-lg); background: var(--content-card); }
.dashboard-history-card-head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px; }
.dashboard-history-card-head h2 { margin: 0; font-size: 16px; }
.dashboard-history-range { font-size: 11px; }
.dashboard-history-loading, .dashboard-history-error { display: grid; min-height: 240px; place-items: center; }
.dashboard-line-legend { display: flex; flex-wrap: wrap; gap: 14px; margin: 18px 0 4px; font-size: 12px; }
.dashboard-line-legend > span { display: inline-flex; align-items: center; gap: 6px; }
.dashboard-line-legend i { display: inline-block; width: 8px; height: 8px; background: currentColor; }
.marker-circle { border-radius: 50%; }
.marker-diamond { transform: rotate(45deg); }
.series-duration, .series-total { color: var(--accent); }
.series-success { color: var(--ok); }
.series-abnormal { color: var(--bad); }
.dashboard-line-chart { display: block; width: 100%; min-width: 0; overflow: visible; }
.chart-unit, .chart-tick { fill: var(--muted); font-size: 12px; }
.chart-grid { stroke: var(--content-card-border); stroke-width: 1; }
.chart-line { fill: none; stroke: currentColor; stroke-width: 2; vector-effect: non-scaling-stroke; }
.line-square { stroke-dasharray: 7 3; }
.line-diamond { stroke-dasharray: 2 4; }
.chart-point { fill: var(--content-card); stroke: currentColor; stroke-width: 2; vector-effect: non-scaling-stroke; }
.chart-day-target { fill: transparent; outline: none; cursor: pointer; }
.chart-day-target:is(:focus, .is-selected) { fill: color-mix(in srgb, var(--accent) 8%, transparent); stroke: var(--accent); stroke-width: 1; stroke-dasharray: 2 4; }
.dashboard-chart-detail { min-height: 32px; margin: 4px 0 0; font-size: 12px; color: var(--muted); }
@media (max-width: 1023px) { .dashboard-history-summary { grid-template-columns: minmax(0, 1fr); } }
@media (max-width: 767px) { .dashboard-history-card { padding: 16px; } .dashboard-line-chart .chart-tick { font-size: 20px; } .dashboard-line-chart .chart-unit { font-size: 18px; } }
</style>
