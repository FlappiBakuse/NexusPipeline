<script setup lang="ts">
import { t } from "../../../platform/i18n";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpScrollArea from "../../../ui/primitives/NxpScrollArea.vue";
import { formatHistoryDate } from "../utils/historyFormat";
import type { HistoryDate, HistoryUser } from "../utils/historyTypes";

/** 日期/用户导航栏：日期展开、用户选择与选中态。请求由页面执行。 */

const props = defineProps<{
  dates: HistoryDate[];
  expanded: Set<string>;
  usersByDate: Map<string, HistoryUser[]>;
  selectedDate: string;
  selectedUserKey: string;
  mobile: boolean;
}>();

const emit = defineEmits<{
  toggleDate: [date: string];
  chooseUser: [date: string, user: HistoryUser];
}>();

function userKeyOf(user: HistoryUser) {
  return String(user.userKey || user.userId || "");
}
function usersOf(date: string) {
  return props.usersByDate.get(date);
}
</script>

<template>
  <aside class="history-dates-panel">
      <div class="history-panel-head">
        <NxpIcon name="calendar" /><h3>{{ t("history.date_list") }}</h3>
        <span class="muted">{{ dates.length }} {{ t("history.days") }}</span>
      </div>
      <NxpScrollArea class="history-dates-list" :aria-label="t('history.date_list')">
        <div
          v-for="item in dates"
          :key="item.date"
          class="history-date-group"
          :class="{ active: expanded.has(item.date) }"
          data-history-date-group
          :data-date="item.date"
          data-testid="history-date-group"
        >
          <NxpButton
            class="history-date-row"
            :class="{ active: expanded.has(item.date) }"
            type="button"
            data-testid="history-date"
            :data-date="item.date"
            :aria-expanded="expanded.has(item.date)"
            @click="emit('toggleDate', item.date)"
          >
            <NxpIcon :name="expanded.has(item.date) ? 'chevronDown' : 'chevronRight'" />
            <span>{{ formatHistoryDate(item.date) }}</span>
            <span class="muted">{{ item.count }} {{ t("common.items") }}</span>
          </NxpButton>
          <div v-if="expanded.has(item.date)" class="history-date-users" :data-date="item.date" data-testid="history-date-users">
            <div v-if="usersOf(item.date) === undefined" class="history-users-loading muted" role="status">{{ t("history.loading_run_users") }}</div>
            <div v-else-if="!usersOf(item.date)?.length" class="history-empty-message">
              <strong>{{ t("history.filter.no_users_day") }}</strong>
              <span>{{ t("history.choose_another_date") }}</span>
            </div>
            <NxpButton
              v-for="user in usersOf(item.date) || []"
              :key="userKeyOf(user)"
              class="history-user-row"
              :class="{ active: item.date === selectedDate && userKeyOf(user) === selectedUserKey }"
              type="button"
              data-testid="history-user"
              :data-history-date="item.date"
              :data-user-key="userKeyOf(user)"
              :data-user-name="user.userName || ''"
              @click="emit('chooseUser', item.date, user)"
            >
              <span class="history-user-avatar" aria-hidden="true"><NxpIcon name="user" /></span>
              <span class="history-user-main">
                <strong>{{ user.userName || t("history.no_user_specified") }}</strong>
                <span v-if="user.userId" class="history-user-id">{{ user.userId }}</span>
                <span class="history-user-badges">
                  <NxpBadge v-if="user.count != null" tone="muted">{{ t("history.summary.runs_short", { count: user.count }) }}</NxpBadge>
                  <NxpBadge v-if="user.successCount" tone="ok">{{ t("history.status.success_short", { count: user.successCount }) }}</NxpBadge>
                  <NxpBadge v-if="user.failedCount" tone="bad">{{ t("history.status.failed_short", { count: user.failedCount }) }}</NxpBadge>
                  <NxpBadge v-if="user.partialCount" tone="warn">{{ t("history.status.partial_short", { count: user.partialCount }) }}</NxpBadge>
                  <NxpBadge v-if="user.cancelledCount" tone="warn">{{ t("history.status.cancelled_short", { count: user.cancelledCount }) }}</NxpBadge>
                  <NxpBadge v-if="user.skippedCount" tone="blue">{{ t("history.status.skipped_short", { count: user.skippedCount }) }}</NxpBadge>
                </span>
              </span>
              <span class="history-user-arrow" aria-hidden="true"><NxpIcon name="chevronRight" /></span>
            </NxpButton>
          </div>
        </div>
        <div v-if="!dates.length" class="history-dates-empty-message">
          <strong>{{ t("history.records.empty_range") }}</strong>
          <span>{{ t("history.filter.date_range_retry") }}</span>
        </div>
      </NxpScrollArea>
  </aside>
</template>
