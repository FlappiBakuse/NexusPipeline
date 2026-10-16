<script setup lang="ts">
import NxpOverflowText from "../../../ui/primitives/NxpOverflowText.vue";
import { computed } from "vue";
import { t } from "../../../platform/i18n";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpScrollArea from "../../../ui/primitives/NxpScrollArea.vue";
import NxpEmptyState from "../../../ui/primitives/NxpEmptyState.vue";
import NxpLoadingState from "../../../ui/composites/NxpLoadingState.vue";
import HistoryDetailModal from "./HistoryDetailModal.vue";
import { formatDateTime, formatDurationMs, historyBadges, statusLabel, statusTone } from "../utils/historyFormat";
import type { HistoryRecord } from "../utils/historyTypes";

const props = defineProps<{
  records: HistoryRecord[];
  selectedUserKey: string;
  selectedUserName: string;
  loading: boolean;
  error: string;
  detail: HistoryRecord | null;
}>();
const emit = defineEmits<{
  retry: [];
  back: [];
  openDetail: [record: HistoryRecord];
  closeDetail: [];
}>();
const panelTitle = computed(() => props.selectedUserKey ? `${props.selectedUserName || t('history.no_user_specified')} · ${t('history.run_records')}` : t('history.run_records'));
</script>

<template>
  <div class="browser-detail-column history-records-column">
    <NxpButton v-if="selectedUserKey" class="browser-detail-back ghost" data-testid="history-user-filter-back" @click="emit('back')">
      {{ t('history.back_to_user_list') }}
    </NxpButton>
    <NxpScrollArea class="browser-detail-pane history-records-pane" :aria-label="panelTitle">
      <NxpEmptyState v-if="!selectedUserKey" :title="t('history.choose_run_users')" :description="t('history.filter.user_day_help')" />
      <template v-else>
        <div class="history-records-heading">
          <h3><NxpOverflowText>{{ selectedUserName || t('history.no_user_specified') }}</NxpOverflowText></h3>
          <NxpBadge v-if="!loading && !error" tone="muted" data-testid="history-records-count">{{ t('history.records.count', { count: records.length }) }}</NxpBadge>
        </div>
        <NxpLoadingState v-if="loading" :title="t('common.loading')" />
        <template v-else-if="error">
          <NxpEmptyState :title="error" tone="danger" />
          <NxpButton @click="emit('retry')">{{ t('common.retry') }}</NxpButton>
        </template>
        <NxpEmptyState v-else-if="!records.length" :title="t('history.records.empty_day')" />
        <div v-else class="history-record-cards">
          <NxpButton
            v-for="(record, index) in records"
            :key="record.id || `${record.startTime}-${record.scriptName}-${index}`"
            class="browser-list-item history-record-card"
            data-testid="history-entry"
            @click="emit('openDetail', record)"
          >
            <span class="history-card-title"><strong><NxpOverflowText>{{ record.scriptName || '-' }}<template v-if="record.queueName"> · {{ record.queueName }}</template></NxpOverflowText></strong><NxpBadge :tone="statusTone(record.status)">{{ statusLabel(record.status) }}</NxpBadge></span>
            <NxpIcon name="chevronRight" class="history-record-arrow" />
            <span class="browser-list-description">{{ formatDateTime(record.startTime) }} · {{ t('history.duration.label') }} {{ formatDurationMs(record.durationMs) }}</span>
            <span class="browser-list-item-meta">
              <NxpBadge v-for="badge in historyBadges(record)" :key="badge.key" :tone="badge.tone" :title="badge.title">{{ badge.label }}</NxpBadge>
              <span class="plugin-slot history-plugin-slot" data-plugin-slot="history.list.badges" data-plugin-anchor="history.list.badges" data-plugin-mode="list" :data-plugin-primary-id="record.id || ''" hidden></span>
            </span>
          </NxpButton>
        </div>
      </template>
    </NxpScrollArea>
    <HistoryDetailModal :record="detail" :fallback-user="selectedUserName" @close="emit('closeDetail')" />
  </div>
</template>
