<script setup lang="ts">
import { computed, ref, watch } from "vue";
import NxpButton from "../primitives/NxpButton.vue";
import NxpIcon from "../primitives/NxpIcon.vue";
import NxpDialogPopover from "./NxpDialogPopover.vue";
import {
  calendarDays,
  canMoveToNextMonth,
  chooseRangeDate,
  dateKey,
  monthKey,
  normalizeDateKey,
  shiftMonth,
  type DateRangeDraft,
  type RangeAnchor,
} from "./dateRange";

const props = withDefaults(defineProps<{
  open: boolean;
  from: string;
  to: string;
  maxDate?: string;
  locale?: string;
  displayId?: string;
  displayTestId?: string;
  popoverId?: string;
  testId?: string;
  dialogLabel?: string;
  toLabel?: string;
  startLabel?: string;
  endLabel?: string;
  applyLabel?: string;
  previousMonthLabel?: string;
  nextMonthLabel?: string;
  dateHelp?: string;
  weekdays?: string[];
}>(), {
  maxDate: "",
  locale: "",
  displayId: "",
  displayTestId: "",
  popoverId: "",
  testId: "",
  dialogLabel: "Choose date range",
  toLabel: "to",
  startLabel: "Start",
  endLabel: "End",
  applyLabel: "Apply",
  previousMonthLabel: "Previous month",
  nextMonthLabel: "Next month",
  dateHelp: "Select a start and end date.",
  weekdays: () => ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"],
});

const emit = defineEmits<{
  open: [];
  close: [];
  apply: [range: DateRangeDraft];
}>();

const today = computed(() => normalizeDateKey(props.maxDate, dateKey()));
const draft = ref<DateRangeDraft>({ from: props.from, to: props.to });
const rangeAnchor = ref<RangeAnchor>("from");
const calendarMonth = ref(monthKey(props.from || today.value));
const calendarMonths = computed(() => [calendarMonth.value, shiftMonth(calendarMonth.value, 1)]);

function localeValue() {
  return props.locale || (typeof navigator === "undefined" ? undefined : navigator.language);
}

function monthLabel(value: string) {
  const [year, month] = monthKey(value).split("-").map(Number);
  return new Date(year, month - 1, 1).toLocaleDateString(localeValue(), { year: "numeric", month: "long" });
}

function dateLabel(value: string) {
  const parsed = new Date(`${value}T00:00:00`);
  return Number.isNaN(parsed.getTime())
    ? value
    : parsed.toLocaleDateString(localeValue(), { year: "numeric", month: "short", day: "numeric" });
}

function displayDate(value: string) {
  return value.replaceAll("-", "/");
}

const calendarTitle = computed(() => `${monthLabel(calendarMonths.value[0])} — ${monthLabel(calendarMonths.value[1])}`);
const canNextMonth = computed(() => canMoveToNextMonth(calendarMonths.value[1], today.value));
const rangeDisplay = computed(() => {
  const range = props.open ? draft.value : props;
  return `${displayDate(range.from)} ${props.toLabel} ${displayDate(range.to)}`;
});

function resetDraft() {
  draft.value = { from: props.from, to: props.to };
  rangeAnchor.value = "from";
  calendarMonth.value = monthKey(props.from || today.value);
}

function selectDate(value: string) {
  const result = chooseRangeDate(draft.value, rangeAnchor.value, value, today.value);
  draft.value = result.draft;
  rangeAnchor.value = result.anchor;
}

function moveCalendar(offset: number) {
  if (offset > 0 && !canNextMonth.value) return;
  calendarMonth.value = shiftMonth(calendarMonth.value, offset);
}

function applyRange() {
  const from = normalizeDateKey(draft.value.from);
  const to = normalizeDateKey(draft.value.to);
  if (!from || !to || from > to || to > today.value) return;
  emit("apply", { from, to });
  emit("close");
}

watch(() => props.open, value => {
  if (value) resetDraft();
});
</script>

<template>
  <div class="nxp-date-range-search" :data-testid="props.testId || undefined" data-nxp-date-range>
    <div class="nxp-date-range-picker">
      <button
        :id="props.displayId || undefined"
        :data-testid="props.displayTestId || undefined"
        class="nxp-date-range-display"
        type="button"
        aria-haspopup="dialog"
        :aria-expanded="props.open"
        :aria-controls="props.popoverId || undefined"
        data-nxp-date-range-display
        @click.stop="props.open ? emit('close') : emit('open')"
      >
        <span data-nxp-date-range-label>{{ rangeDisplay }}</span>
        <span class="nxp-date-range-icon" aria-hidden="true"><NxpIcon name="calendar" /></span>
      </button>
      <NxpDialogPopover
        :open="props.open"
        :id="props.popoverId || undefined"
        class="nxp-date-range-popover secondary-surface"
        :aria-label="props.dialogLabel"
        :closeable="false"
        @close="emit('close')"
      >
        <div class="nxp-date-range-toolbar">
          <button class="ghost sm" type="button" :aria-label="props.previousMonthLabel" @click="moveCalendar(-1)">‹</button>
          <strong>{{ calendarTitle }}</strong>
          <button class="ghost sm" type="button" :aria-label="props.nextMonthLabel" :disabled="!canNextMonth" @click="moveCalendar(1)">›</button>
        </div>
        <div class="nxp-date-range-months">
          <section v-for="month in calendarMonths" :key="month" class="nxp-date-range-month">
            <h4>{{ monthLabel(month) }}</h4>
            <div class="nxp-date-range-grid">
              <span v-for="(weekday, index) in props.weekdays" :key="`${month}-${weekday}-${index}`" class="nxp-date-range-weekday">{{ weekday }}</span>
              <template v-for="(day, index) in calendarDays(month, draft, today)" :key="`${month}-${index}`">
                <span v-if="day.empty" class="nxp-date-range-day is-empty" aria-hidden="true"></span>
                <button
                  v-else
                  class="nxp-date-range-day"
                  :class="{ 'is-start': day.start, 'is-end': day.end, 'is-in-range': day.inRange }"
                  type="button"
                  :disabled="day.disabled"
                  :aria-label="dateLabel(day.value)"
                  @click="selectDate(day.value)"
                >
                  {{ Number(day.value.slice(-2)) }}
                </button>
              </template>
            </div>
          </section>
        </div>
        <div class="nxp-date-range-selection">
          <span class="nxp-date-range-selection-item"><span class="muted">{{ props.startLabel }}</span><strong>{{ displayDate(draft.from) }}</strong></span>
          <span class="nxp-date-range-selection-arrow" aria-hidden="true">→</span>
          <span class="nxp-date-range-selection-item"><span class="muted">{{ props.endLabel }}</span><strong>{{ displayDate(draft.to) }}</strong></span>
        </div>
        <div class="nxp-date-range-footer">
          <span class="muted nxp-date-range-hint">{{ props.dateHelp }}</span>
          <NxpButton class="primary sm" type="button" @click="applyRange">{{ props.applyLabel }}</NxpButton>
        </div>
      </NxpDialogPopover>
    </div>
  </div>
</template>

<style>
:host { display: block; min-width: 0; }
.nxp-date-range-search { position: relative; min-width: 0; }
.nxp-date-range-picker { position: relative; min-width: 0; }
.nxp-date-range-display { position: relative; display: flex; width: 100%; min-height: 40px; align-items: center; justify-content: space-between; gap: 12px; padding: 0 40px 0 12px; border: 1px solid var(--content-control-border, var(--nx-color-border)); border-radius: 8px; background: var(--content-control, var(--nx-color-surface)); color: var(--text, var(--nx-color-text)); font-size: 13px; font-weight: 400; text-align: left; }
.nxp-date-range-display:hover, .nxp-date-range-display[aria-expanded="true"] { border-color: var(--accent, var(--nx-color-primary)); background: var(--content-control-hover, var(--nx-color-surface)); }
.nxp-date-range-display > [data-nxp-date-range-label] { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.nxp-date-range-icon { position: absolute; top: 50%; right: 12px; display: inline-flex; pointer-events: none; transform: translateY(-50%); color: var(--accent, var(--nx-color-primary)); }
.nxp-date-range-icon .icon, .nxp-date-range-icon .nxp-icon { width: 16px; height: 16px; }
.nxp-date-range-popover { position: absolute; top: calc(100% + 8px); left: 0; z-index: 70; display: grid; container-name: nxp-date-range-popover; container-type: inline-size; min-width: min(100%, 520px); gap: var(--space-3, var(--nx-space-3, 12px)); padding: var(--space-3, var(--nx-space-3, 12px)); border: 1px solid var(--content-control-border, var(--nx-color-border)); border-radius: var(--radius-md, var(--nx-radius-md, 8px)); background: var(--content-card, var(--nx-color-surface)); box-shadow: var(--shadow, 0 16px 40px rgb(0 0 0 / 28%)); }
.nxp-date-range-toolbar { display: grid; grid-template-columns: 40px minmax(0, 1fr) 40px; align-items: center; gap: var(--space-2, var(--nx-space-2, 8px)); }
.nxp-date-range-toolbar strong { min-width: 0; color: var(--text, var(--nx-color-text)); font-size: 13px; text-align: center; }
.nxp-date-range-toolbar button { min-width: 40px; min-height: 36px; padding: 0; font-size: 20px; line-height: 1; }
.nxp-date-range-months { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-3, var(--nx-space-3, 12px)); }
.nxp-date-range-month { min-width: 0; }
.nxp-date-range-month h4 { margin: 0 0 8px; color: var(--text, var(--nx-color-text)); font-size: 12px; text-align: center; }
.nxp-date-range-grid { display: grid; grid-template-columns: repeat(7, minmax(32px, 1fr)); gap: 3px; }
.nxp-date-range-weekday { display: grid; min-height: 24px; place-items: center; color: var(--faint, var(--nx-color-muted)); font-size: 10px; font-weight: 700; }
.nxp-date-range-day { display: grid; min-width: 0; min-height: 32px; place-items: center; padding: 0; border: 1px solid transparent; border-radius: 6px; background: transparent; color: var(--text, var(--nx-color-text)); font-size: 12px; box-shadow: none; }
.nxp-date-range-day:hover:not(:disabled), .nxp-date-range-day:focus-visible { border-color: var(--accent, var(--nx-color-primary)); background: var(--content-control-hover, var(--nx-color-surface)); color: var(--accent, var(--nx-color-primary)); }
.nxp-date-range-day.is-in-range { border-radius: 0; background: var(--accent-soft, rgb(100 120 255 / 18%)); color: var(--accent, var(--nx-color-primary)); }
.nxp-date-range-day.is-start { border-radius: 6px 0 0 6px; background: var(--accent, var(--nx-color-primary)); color: var(--on-accent, #fff); }
.nxp-date-range-day.is-end { border-radius: 0 6px 6px 0; background: var(--accent, var(--nx-color-primary)); color: var(--on-accent, #fff); }
.nxp-date-range-day.is-start.is-end { border-radius: 6px; }
.nxp-date-range-day:disabled { cursor: not-allowed; color: var(--faint, var(--nx-color-muted)); opacity: .42; }
.nxp-date-range-day.is-empty { pointer-events: none; }
.nxp-date-range-selection { display: flex; align-items: center; justify-content: center; gap: var(--space-3, var(--nx-space-3, 12px)); padding-top: var(--space-2, var(--nx-space-2, 8px)); border-top: 1px solid var(--border, var(--nx-color-border)); }
.nxp-date-range-selection-item { display: grid; min-width: 0; gap: 2px; text-align: center; }
.nxp-date-range-selection-item .muted { font-size: 10px; }
.nxp-date-range-selection-item strong { color: var(--text, var(--nx-color-text)); font-size: 12px; }
.nxp-date-range-selection-arrow { color: var(--accent, var(--nx-color-primary)); }
.nxp-date-range-footer { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3, var(--nx-space-3, 12px)); }
.nxp-date-range-hint { font-size: 11px; }
@media (max-width: 560px) {
  .nxp-date-range-footer { align-items: stretch; flex-direction: column; }
  .nxp-date-range-footer .primary { width: 100%; }
}
@container nxp-date-range-popover (max-width: 495px) {
  .nxp-date-range-months { grid-template-columns: minmax(0, 1fr); }
}
</style>
