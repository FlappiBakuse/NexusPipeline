<script setup lang="ts">
import { computed, ref } from 'vue';
import type { RecordSummary, RecordTotals } from './dashboard-types';
import { locale, t } from '../app/locale';
import './dashboard-messages';
const props = defineProps<{
  summary: RecordSummary | null;
  loading: boolean;
  error: string;
  dates: string[];
  kind?: 'duration' | 'runs';
}>();
const selected = ref<{ chart: 'duration' | 'runs'; day: number } | null>(null);
const width = 560,
  height = 260,
  left = 58,
  right = 58,
  top = 24,
  bottom = 40;
const zero: RecordTotals = {
  runs: 0,
  succeeded: 0,
  failed: 0,
  cancelled: 0,
  unverified: 0,
  measuredCount: 0,
  measuredMilliseconds: 0,
  lowerBoundCount: 0,
  unknownDurationCount: 0
};
const days = computed(() =>
  props.dates.map((date) => ({
    date,
    totals: props.summary?.days.find((day) => day.date === date)?.totals || zero
  }))
);
const durationUnit = computed(() => {
  const maximum = Math.max(0, ...days.value.map((day) => day.totals.measuredMilliseconds));
  return maximum >= 3600000
    ? { value: 3600000, label: t('小时') }
    : maximum >= 60000
      ? { value: 60000, label: t('分钟') }
      : { value: 1000, label: t('秒') };
});
const charts = computed(() => [
  {
    id: 'duration' as const,
    title: t('每日运行总时长'),
    unit: durationUnit.value.label,
    series: [
      {
        id: 'duration',
        title: t('累计耗时'),
        values: days.value.map((day) => day.totals.measuredMilliseconds / durationUnit.value.value)
      }
    ]
  },
  {
    id: 'runs' as const,
    title: t('每日运行次数'),
    unit: t('次'),
    series: [
      { id: 'all', title: t('总次数'), values: days.value.map((day) => day.totals.runs) },
      { id: 'success', title: t('成功次数'), values: days.value.map((day) => day.totals.succeeded) },
      {
        id: 'other',
        title: t('异常次数'),
        values: days.value.map((day) => day.totals.runs - day.totals.succeeded)
      }
    ]
  }
]);
function maximum(chart: (typeof charts.value)[number]): number {
  const value = Math.max(0, ...chart.series.flatMap((item) => item.values));
  if (value <= 0) return chart.id === 'duration' ? 1 : 4;
  const step = 10 ** Math.floor(Math.log10(value / 4));
  return Math.ceil(value / step / 4) * step * 4;
}
function x(index: number): number {
  return left + (index * (width - left - right)) / 6;
}
function y(value: number, max: number): number {
  return height - bottom - (value / max) * (height - top - bottom);
}
function points(values: number[], max: number): string {
  return values.map((value, index) => `${x(index)},${y(value, max)}`).join(' ');
}
function dateText(date?: string): string {
  return date
    ? new Date(date + 'T00:00:00').toLocaleDateString(locale.value, {
        year: 'numeric',
        month: 'long',
        day: 'numeric'
      })
    : '';
}
function detail(chart: 'duration' | 'runs', index: number): string {
  const day = days.value[index];
  if (!day) return '';
  const totals = day.totals;
  return chart === 'duration'
    ? `${day.date}: ${t('已测')} ${formatTime(totals.measuredMilliseconds)}, ${t('完整测量')} ${totals.measuredCount} ${t('次')}, ${t('耗时下界')} ${totals.lowerBoundCount} ${t('次')}, ${t('未知')} ${totals.unknownDurationCount} ${t('次')}.`
    : `${day.date}: ${t('全部')} ${totals.runs} ${t('次')}, ${t('业务成功')} ${totals.succeeded} ${t('次')}, ${t('其他')} ${totals.runs - totals.succeeded} ${t('次')}.`;
}
function formatTime(milliseconds: number): string {
  if (milliseconds >= 3600000) return `${(milliseconds / 3600000).toFixed(1)} ${t('小时')}`;
  if (milliseconds >= 60000) return `${(milliseconds / 60000).toFixed(1)} ${t('分钟')}`;
  return `${(milliseconds / 1000).toFixed(1)} ${t('秒')}`;
}
function key(event: KeyboardEvent, chart: 'duration' | 'runs', index: number): void {
  const next =
    event.key === 'Home'
      ? 0
      : event.key === 'End'
        ? 6
        : event.key === 'ArrowLeft'
          ? Math.max(0, index - 1)
          : event.key === 'ArrowRight'
            ? Math.min(6, index + 1)
            : index;
  if (next === index && !['Enter', ' '].includes(event.key)) return;
  event.preventDefault();
  selected.value = { chart, day: next };
  (event.currentTarget as SVGElement).ownerSVGElement
    ?.querySelector<SVGElement>(`[data-chart-day="${next}"]`)
    ?.focus();
}
</script>

<template>
  <div class="history-summary" :class="{ single: !!kind }">
    <section
      v-for="chart in charts.filter((item) => !kind || item.id === kind)"
      :key="chart.id"
      class="history-chart"
      :aria-label="chart.title"
    >
      <header>
        <h3>{{ chart.title }}</h3>
        <small>{{ dateText(dates[0]) }} {{ t('至') }} {{ dateText(dates[6]) }}</small>
      </header>
      <p v-if="loading && !summary" role="status">{{ t('正在读取历史统计…') }}</p>
      <p v-else-if="error || !summary" role="alert">{{ t(error || '历史统计不可用。') }}</p>
      <template v-else>
        <div class="legend">
          <span v-for="series in chart.series" :key="series.id" :class="'series-' + series.id">
            {{ series.title }}
          </span>
        </div>
        <svg
          :viewBox="`0 0 ${width} ${height}`"
          role="group"
          :aria-label="chart.title"
          @mouseleave="selected = null"
        >
          <text :x="left" :y="top - 11" aria-hidden="true">{{ chart.unit }}</text>
          <g v-for="tick in [0, 1, 2, 3, 4]" :key="tick" aria-hidden="true">
            <line
              :x1="left"
              :x2="width - right"
              :y1="y((maximum(chart) * tick) / 4, maximum(chart))"
              :y2="y((maximum(chart) * tick) / 4, maximum(chart))"
            />
            <text :x="left - 10" :y="y((maximum(chart) * tick) / 4, maximum(chart)) + 4" text-anchor="end">
              {{ Number(((maximum(chart) * tick) / 4).toFixed(2)) }}
            </text>
          </g>
          <g
            v-for="series in chart.series"
            :key="series.id"
            :class="'series-' + series.id"
            aria-hidden="true"
          >
            <polyline :points="points(series.values, maximum(chart))" />
            <g v-for="(value, index) in series.values" :key="index">
              <rect
                v-if="series.id === 'success'"
                class="point"
                :x="x(index) - 3.5"
                :y="y(value, maximum(chart)) - 3.5"
                width="7"
                height="7"
              />
              <path
                v-else-if="series.id === 'other'"
                class="point"
                :d="`M${x(index)} ${y(value, maximum(chart)) - 5}l5 5-5 5-5-5z`"
              />
              <circle v-else class="point" :cx="x(index)" :cy="y(value, maximum(chart))" r="4" />
            </g>
          </g>
          <g v-for="(day, index) in days" :key="day.date">
            <text :x="x(index)" :y="height - 14" text-anchor="middle" aria-hidden="true">
              {{ day.date.slice(5).replace('-', '/') }}
            </text>
            <rect
              class="day-hit"
              :class="{ active: selected?.chart === chart.id && selected.day === index }"
              :x="x(index) - 28"
              :y="top"
              width="56"
              :height="height - top - bottom"
              tabindex="0"
              role="button"
              :data-chart-day="index"
              :aria-label="detail(chart.id, index)"
              @focus="selected = { chart: chart.id, day: index }"
              @pointerenter="selected = { chart: chart.id, day: index }"
              @keydown="key($event, chart.id, index)"
            />
          </g>
        </svg>
        <p class="chart-detail" aria-live="polite">
          {{
            selected?.chart === chart.id
              ? detail(chart.id, selected.day)
              : chart.id === 'duration'
                ? `7 ${t('天累计时长：')} ${summary?.totals.measuredMilliseconds ? formatTime(summary.totals.measuredMilliseconds) : '0 ' + t('毫秒')}`
                : `7 ${t('天汇总 · 总次数')} ${summary?.totals.runs || 0} · ${t('成功')} ${summary?.totals.succeeded || 0} · ${t('异常')} ${(summary?.totals.runs || 0) - (summary?.totals.succeeded || 0)}`
          }}
        </p>
      </template>
    </section>
  </div>
</template>

<style scoped src="./dashboard-history-chart.css"></style>
