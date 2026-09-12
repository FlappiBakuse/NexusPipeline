<script setup lang="ts">
import { computed } from "vue";
import { t } from "../../../platform/i18n";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpScrollArea from "../../../ui/primitives/NxpScrollArea.vue";
import HistoryDetailModal from "./HistoryDetailModal.vue";
import { formatDateTime, historyBadges, statusLabel, statusTone } from "../utils/historyFormat";
import type { HistoryRecord } from "../utils/historyTypes";

/** 历史记录栏：记录列表、详情弹窗与运行记录计数。请求由页面执行。 */

const props = defineProps<{
  records: HistoryRecord[];
  historyDir: string;
  selectedDate: string;
  selectedUserKey: string;
  selectedUserName: string;
  detail: HistoryRecord | null;
}>();

const emit = defineEmits<{
  refresh: [];
  back: [];
  openDetail: [record: HistoryRecord];
  closeDetail: [];
}>();

const panelTitle = computed(() => props.selectedUserKey ? `${props.selectedUserName || t("history.no_user_specified")} · ${t("history.run_records")}` : t("history.run_records"));
const panelCount = computed(() => props.selectedUserKey ? t("history.records.count", { count: props.records.length }) : t("history.choose_user"));

function recordKey(record: HistoryRecord, index: number) {
  return record.id || `${record.startTime}-${record.scriptName}-${index}`;
}
function recordPath(record: HistoryRecord) {
  return [props.historyDir, props.selectedDate, record.historyDirectory, record.logFile].filter(Boolean).join("\\");
}
</script>

<template>
  <div class="history-records-column">
    <NxpButton v-if="props.selectedUserKey" class="history-detail-back ghost" type="button" data-testid="history-user-filter-back" @click="emit('back')">
      {{ t("history.back_to_user_list") }}
    </NxpButton>
    <section class="history-records-panel history-level-panel">
      <div class="history-panel-head">
        <NxpIcon :name="props.selectedUserKey ? 'queues' : props.selectedDate ? 'scripts' : 'history'" /><h3>{{ panelTitle }}</h3>
        <span class="muted" data-testid="history-records-count">{{ panelCount }}</span>
        <button class="history-refresh" type="button" :aria-label="t('history.refresh_records')" data-testid="history-refresh" @click="emit('refresh')">
          <NxpIcon name="refresh" />
        </button>
      </div>
      <NxpScrollArea class="history-entry-list history-level-list" :aria-label="panelTitle">
        <div v-if="!props.selectedUserKey" class="history-empty-message">
          <strong>{{ t("history.choose_run_users") }}</strong>
          <span>{{ t("history.filter.user_day_help") }}</span>
        </div>
        <div v-else-if="!props.records.length" class="history-empty-message">{{ t("history.records.empty_day") }}</div>
        <button
          v-for="(record, index) in props.records"
          v-else
          :key="recordKey(record, index)"
          class="history-entry"
          :class="`history-status-${record.status || 'failed'}`"
          type="button"
          data-testid="history-entry"
          @click="emit('openDetail', record)"
        >
          <span class="history-entry-bar" aria-hidden="true"></span>
          <span class="history-entry-main">
            <span class="history-entry-title">
              <strong>{{ formatDateTime(record.startTime) }} · {{ record.scriptName || "-" }}<template v-if="record.queueName"> · {{ record.queueName }}</template></strong>
              <NxpBadge :tone="statusTone(record.status)">{{ statusLabel(record.status) }}</NxpBadge>
              <NxpBadge v-for="badge in historyBadges(record)" :key="badge.key" :tone="badge.tone" :title="badge.title">{{ badge.label }}</NxpBadge>
              <span
                class="plugin-slot history-plugin-slot"
                data-plugin-slot="history.list.badges"
                data-plugin-anchor="history.list.badges"
                data-plugin-mode="list"
                :data-plugin-primary-id="record.id || ''"
                hidden
              ></span>
            </span>
            <span class="history-entry-path">{{ recordPath(record) }}</span>
          </span>
          <span class="history-entry-arrow" aria-hidden="true"><NxpIcon name="chevronRight" /></span>
        </button>
      </NxpScrollArea>
    </section>
    <HistoryDetailModal :record="props.detail" :fallback-user="props.selectedUserName" @close="emit('closeDetail')" />
  </div>
</template>
