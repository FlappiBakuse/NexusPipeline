<script setup lang="ts">
import { getLocale, t } from "../../../platform/i18n";
import NxpDateRangePicker from "../../../ui/composites/NxpDateRangePicker.vue";
import type { DateRangeDraft } from "../../../ui/composites/dateRange";

const props = defineProps<{
  open: boolean;
  from: string;
  to: string;
}>();

const emit = defineEmits<{
  open: [];
  close: [];
  apply: [range: DateRangeDraft];
}>();
</script>

<template>
  <div class="history-range-search" data-history-range data-testid="history-range-search">
    <NxpDateRangePicker
      class="history-range-picker"
      :open="props.open"
      :from="props.from"
      :to="props.to"
      :locale="getLocale()"
      popover-class="history-range-popover"
      display-id="history-range-display"
      display-test-id="history-range-display"
      popover-id="history-range-popover"
      :dialog-label="t('history.choose_time_range')"
      :to-label="t('common.to')"
      :start-label="t('common.start')"
      :end-label="t('common.end')"
      :apply-label="t('history.apply_range')"
      :previous-month-label="t('history.previous_month')"
      :next-month-label="t('history.next_month')"
      :date-help="t('history.filter.date_help')"
      :weekdays="[t('common.sun'), t('common.mon'), t('common.tue'), t('common.wed'), t('common.thu'), t('common.fri'), t('common.sat')]"
      @open="emit('open')"
      @close="emit('close')"
      @apply="emit('apply', $event)"
    />
    <input id="history-from" type="hidden" :value="props.from" :aria-label="`${t('common.start')} ${t('common.date')}`" data-testid="history-from">
    <input id="history-to" type="hidden" :value="props.to" :aria-label="`${t('common.end')} ${t('common.date')}`" data-testid="history-to">
  </div>
</template>
