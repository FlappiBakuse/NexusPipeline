<script setup lang="ts">
import NxpOverflowText from "../../../ui/primitives/NxpOverflowText.vue";
import { t } from "../../../platform/i18n";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpScrollArea from "../../../ui/primitives/NxpScrollArea.vue";
import NxpEmptyState from "../../../ui/primitives/NxpEmptyState.vue";
import NxpCollapsibleCard from "../../../ui/composites/NxpCollapsibleCard.vue";
import NxpLoadingState from "../../../ui/composites/NxpLoadingState.vue";
import { formatHistoryDate } from "../utils/historyFormat";
import type { HistoryDate, HistoryUser } from "../utils/historyTypes";

const props = defineProps<{
  dates: HistoryDate[];
  loading: boolean;
  error: string;
  expanded: Set<string>;
  usersByDate: Map<string, HistoryUser[]>;
  userErrors: Map<string, string>;
  selectedDate: string;
  selectedUserKey: string;
}>();
const emit = defineEmits<{
  retry: [];
  toggleDate: [date: string];
  chooseUser: [date: string, user: HistoryUser];
  retryUsers: [date: string];
}>();
function userKeyOf(user: HistoryUser) {
  return String(user.userKey || user.userId || "");
}
</script>

<template>
  <NxpScrollArea class="browser-list-pane" :aria-label="t('history.date_list')">
    <NxpLoadingState v-if="loading" :title="t('common.loading')" />
    <div v-else-if="error" class="browser-list">
      <NxpEmptyState :title="error" tone="danger" />
      <NxpButton @click="emit('retry')">{{ t('common.retry') }}</NxpButton>
    </div>
    <div v-else class="browser-list">
      <NxpCollapsibleCard
        v-for="item in dates"
        :key="item.date"
        class="history-date-card"
        surface="secondary"
        :title="formatHistoryDate(item.date)"
        :expanded="expanded.has(item.date)"
        :panel-id="`history-date-${item.date}`"
        :data-date="item.date"
        data-testid="history-date-group"
        @toggle="emit('toggleDate', item.date)"
      >
        <div v-if="expanded.has(item.date)" class="history-date-users" data-testid="history-date-users">
          <template v-if="userErrors.has(item.date)">
            <NxpEmptyState :title="userErrors.get(item.date) || ''" tone="danger" />
            <NxpButton @click="emit('retryUsers', item.date)">{{ t('common.retry') }}</NxpButton>
          </template>
          <NxpLoadingState v-else-if="!usersByDate.has(item.date)" :title="t('history.loading_run_users')" />
          <NxpEmptyState v-else-if="!usersByDate.get(item.date)?.length" :title="t('history.filter.no_users_day')" />
          <NxpButton
            v-for="user in usersByDate.get(item.date) || []"
            :key="userKeyOf(user)"
            class="browser-list-item history-user-card"
            :class="{ 'is-selected': item.date === selectedDate && userKeyOf(user) === selectedUserKey }"
            :aria-pressed="item.date === selectedDate && userKeyOf(user) === selectedUserKey"
            data-testid="history-user"
            :data-history-date="item.date"
            :data-user-key="userKeyOf(user)"
            @click="emit('chooseUser', item.date, user)"
          >
            <span class="history-card-title"><NxpIcon name="user" /><strong><NxpOverflowText>{{ user.userName || t('history.no_user_specified') }}</NxpOverflowText></strong><NxpIcon name="chevronRight" /></span>
          </NxpButton>
        </div>
      </NxpCollapsibleCard>
      <NxpEmptyState v-if="!dates.length" :title="t('history.records.empty_range')" :description="t('history.filter.date_range_retry')" />
    </div>
  </NxpScrollArea>
</template>
