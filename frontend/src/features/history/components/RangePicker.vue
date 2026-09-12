<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { getLocale, t } from "../../../platform/i18n";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpDialogPopover from "../../../ui/composites/NxpDialogPopover.vue";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import { formatHistoryDate, historyMonthKey, historyTodayValue } from "../utils/historyFormat";

/** 历史时间范围选择：隐藏输入承载 from/to，浮层为二级页面语义的 `role="dialog"`。 */

const props = defineProps<{
  open: boolean;
  from: string;
  to: string;
}>();

const emit = defineEmits<{
  open: [];
  close: [];
  apply: [range: { from: string; to: string }];
}>();

const today = historyTodayValue();
const rangeDraftFrom = ref(props.from);
const rangeDraftTo = ref(props.to);
const rangeAnchor = ref<"from" | "to">("from");
const calendarMonth = ref(historyMonthKey(props.from));

function shiftMonth(value: string, offset: number) {
  const [year, month] = historyMonthKey(value).split("-").map(Number);
  const date = new Date(year, month - 1 + offset, 1);
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}`;
}
function monthLabel(value: string) {
  const [year, month] = historyMonthKey(value).split("-").map(Number);
  return new Date(year, month - 1, 1).toLocaleDateString(getLocale(), { year: "numeric", month: "long" });
}

const calendarMonths = computed(() => [calendarMonth.value, shiftMonth(calendarMonth.value, 1)]);
const calendarTitle = computed(() => `${monthLabel(calendarMonths.value[0])} — ${monthLabel(calendarMonths.value[1])}`);
const canNextMonth = computed(() => calendarMonths.value[1] < historyMonthKey(today));
const rangeDisplay = computed(() => `${rangeDraftFrom.value.replaceAll("-", "/")} ${t("common.to")} ${rangeDraftTo.value.replaceAll("-", "/")}`);

function calendarDays(value: string) {
  const [year, month] = historyMonthKey(value).split("-").map(Number);
  const first = new Date(year, month - 1, 1);
  const count = new Date(year, month, 0).getDate();
  const days: Array<{ value: string; empty: boolean; inRange: boolean; start: boolean; end: boolean }> = [];
  const pad = (input: number) => String(input).padStart(2, "0");
  for (let index = 0; index < first.getDay(); index += 1) days.push({ value: "", empty: true, inRange: false, start: false, end: false });
  for (let day = 1; day <= count; day += 1) {
    const date = `${year}-${pad(month)}-${pad(day)}`;
    days.push({
      value: date,
      empty: false,
      inRange: Boolean(rangeDraftFrom.value && rangeDraftTo.value && date > rangeDraftFrom.value && date < rangeDraftTo.value),
      start: date === rangeDraftFrom.value,
      end: date === rangeDraftTo.value,
    });
  }
  return days;
}

function chooseRangeDate(value: string) {
  if (!value || value > today) return;
  if (rangeAnchor.value === "from") {
    rangeDraftFrom.value = value;
    if (rangeDraftTo.value && rangeDraftTo.value < value) rangeDraftTo.value = value;
    rangeAnchor.value = "to";
    return;
  }
  if (value < rangeDraftFrom.value) {
    rangeDraftTo.value = rangeDraftFrom.value;
    rangeDraftFrom.value = value;
  } else {
    rangeDraftTo.value = value;
  }
  rangeAnchor.value = "from";
}
function moveCalendar(offset: number) {
  if (offset > 0 && !canNextMonth.value) return;
  calendarMonth.value = shiftMonth(calendarMonth.value, offset);
}
function applyRange() {
  if (!rangeDraftFrom.value || !rangeDraftTo.value || rangeDraftFrom.value > rangeDraftTo.value) return;
  emit("apply", { from: rangeDraftFrom.value, to: rangeDraftTo.value });
  emit("close");
}

watch(() => props.open, value => {
  if (!value) return;
  rangeDraftFrom.value = props.from;
  rangeDraftTo.value = props.to;
  rangeAnchor.value = "from";
  calendarMonth.value = historyMonthKey(props.from);
});
</script>

<template>
  <div class="history-range-search" data-history-range data-testid="history-range-search">
    <div class="history-range-picker">
      <button
        id="history-range-display"
        class="history-range-display"
        type="button"
        aria-haspopup="dialog"
        :aria-expanded="props.open"
        aria-controls="history-range-popover"
        data-history-range-display
        data-testid="history-range-display"
        @click.stop="props.open ? emit('close') : emit('open')"
      >
        <span data-history-range-label>{{ rangeDisplay }}</span>
        <span class="history-range-icon" aria-hidden="true"><NxpIcon name="calendar" /></span>
      </button>
      <NxpDialogPopover
        :open="props.open"
        id="history-range-popover"
        class="history-range-popover secondary-surface"
        :aria-label="t('history.choose_time_range')"
        :close-label="t('common.close')"
        @close="emit('close')"
      >
        <div class="history-calendar-toolbar">
          <button class="ghost sm" type="button" :aria-label="t('history.previous_month')" @click="moveCalendar(-1)">‹</button>
          <strong>{{ calendarTitle }}</strong>
          <button class="ghost sm" type="button" :aria-label="t('history.next_month')" :disabled="!canNextMonth" @click="moveCalendar(1)">›</button>
        </div>
        <div class="history-calendar-months">
          <section v-for="month in calendarMonths" :key="month" class="history-calendar-month">
            <h4>{{ monthLabel(month) }}</h4>
            <div class="history-calendar-grid">
              <span v-for="day in [t('common.sun'), t('common.mon'), t('common.tue'), t('common.wed'), t('common.thu'), t('common.fri'), t('common.sat')]" :key="`${month}-${day}`" class="history-calendar-weekday">{{ day }}</span>
              <template v-for="(day, index) in calendarDays(month)" :key="`${month}-${index}`">
                <span v-if="day.empty" class="history-calendar-day is-empty" aria-hidden="true"></span>
                <button
                  v-else
                  class="history-calendar-day"
                  :class="{ 'is-start': day.start, 'is-end': day.end, 'is-in-range': day.inRange }"
                  type="button"
                  :disabled="day.value > today"
                  :aria-label="formatHistoryDate(day.value)"
                  @click="chooseRangeDate(day.value)"
                >
                  {{ Number(day.value.slice(-2)) }}
                </button>
              </template>
            </div>
          </section>
        </div>
        <div class="history-range-selection">
          <span class="history-range-selection-item"><span class="muted">{{ t("common.start") }}</span><strong>{{ rangeDraftFrom.replaceAll("-", "/") }}</strong></span>
          <span class="history-range-selection-arrow" aria-hidden="true">→</span>
          <span class="history-range-selection-item"><span class="muted">{{ t("common.end") }}</span><strong>{{ rangeDraftTo.replaceAll("-", "/") }}</strong></span>
        </div>
        <div class="history-range-popover-footer">
          <span class="muted history-range-hint">{{ t("history.filter.date_help") }}</span>
          <NxpButton class="primary sm" type="button" @click="applyRange">{{ t("history.apply_range") }}</NxpButton>
        </div>
      </NxpDialogPopover>
      <input id="history-from" type="hidden" :value="props.from" :aria-label="`${t('common.start')} ${t('common.date')}`" data-testid="history-from">
      <input id="history-to" type="hidden" :value="props.to" :aria-label="`${t('common.end')} ${t('common.date')}`" data-testid="history-to">
    </div>
  </div>
</template>
