<script setup lang="ts">
import { computed, markRaw, onBeforeUnmount, reactive, ref, watch, type Raw } from 'vue';
import type { AppStore } from '../app/store';
import { arrayValue, objectValue } from '../control/client';
import { t, locale } from '../app/locale';
import { UserWindow } from './user-window';
const props = defineProps<{
  store: AppStore;
  days: Record<string, unknown>[];
  visible: boolean;
  selectedDate: string;
  selectedUser: string;
  business: string;
  lifecycle: string;
  loading: boolean;
  error: string;
}>();
const emit = defineEmits<{
  select: [date: string, user: string, total: number | null];
  count: [date: string, user: string, total: number | null];
  retry: [];
}>();
type Window = {
  items: Record<string, unknown>[];
  next: string | null;
  index: Raw<UserWindow>;
  page: number;
  error: string;
  busy: boolean;
};
const expanded = ref(new Set<string>());
const windows = ref(new Map<string, Window>());
const owners = new Map<string, AbortController>();
const elements = new Map<Element, string>();
const visibleDates = new Set<string>();
const users = computed(() => props.store.state.automation?.bundle.users || []);
const observer = new IntersectionObserver(
  (entries) => {
    for (const entry of entries) {
      const date = elements.get(entry.target);
      if (!date) continue;
      if (entry.isIntersecting) {
        visibleDates.add(date);
        if (expanded.value.has(date)) void load(date);
      } else visibleDates.delete(date);
    }
  },
  { rootMargin: '120px' }
);
function track(element: unknown, date: string): void {
  if (!element) {
    for (const [previous, value] of elements)
      if (value === date) {
        observer.unobserve(previous);
        elements.delete(previous);
      }
    visibleDates.delete(date);
    return;
  }
  if (!(element instanceof Element) || elements.has(element)) return;
  elements.set(element, date);
  observer.observe(element);
}
function dateText(date: string): string {
  return new Date(date + 'T00:00:00').toLocaleDateString(locale.value, {
    year: 'numeric',
    month: 'long',
    day: 'numeric'
  });
}
function userTotal(user: Record<string, unknown>): number | null {
  if (!user.totals || typeof user.totals !== 'object' || Array.isArray(user.totals)) return null;
  const value = (user.totals as Record<string, unknown>).runs;
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null;
}
async function load(date: string, page = 1): Promise<void> {
  if (!props.visible || !expanded.value.has(date) || owners.has(date)) return;
  const previous = windows.value.get(date);
  if (previous && previous.page === page && !previous.error) return;
  const state: Window =
    previous ||
    reactive({ items: [], next: null, index: markRaw(new UserWindow()), page: 1, error: '', busy: false });
  state.busy = true;
  state.error = '';
  windows.value.set(date, state);
  const owner = new AbortController();
  owners.set(date, owner);
  try {
    const start = new Date(date + 'T00:00:00'),
      end = new Date(start);
    end.setDate(end.getDate() + 1);
    const query = new URLSearchParams({
      endedFromInclusive: start.toISOString(),
      endedUntilExclusive: end.toISOString(),
      offsetMinutes: String(-start.getTimezoneOffset())
    });
    if (props.business) query.set('business', props.business);
    if (props.lifecycle) query.set('lifecycle', props.lifecycle);
    const result = await state.index.read(
      page,
      async (cursor) => {
        const requested = new URLSearchParams(query);
        if (cursor) requested.set('afterUserId', cursor);
        const value = await props.store.client.read(
          '/control/v1/records/summary?' + requested,
          objectValue,
          owner.signal
        );
        return {
          items: arrayValue(value.users, objectValue),
          next: value.nextUserId == null ? null : String(value.nextUserId)
        };
      },
      owner.signal
    );
    owner.signal.throwIfAborted();
    state.items = result.data.items;
    state.page = result.number;
    state.next = result.data.next;
    const selected = state.items.find((item) => String(item.userId || 'null') === props.selectedUser);
    if (selected) emit('count', date, props.selectedUser, userTotal(selected));
    windows.value.delete(date);
    windows.value.set(date, state);
    while (windows.value.size > 8) {
      const victim = [...windows.value.keys()].find((key) => key !== date && !visibleDates.has(key));
      if (!victim) break;
      owners.get(victim)?.abort();
      windows.value.delete(victim);
    }
  } catch (reason) {
    if (!owner.signal.aborted) state.error = reason instanceof Error ? reason.message : String(reason);
  } finally {
    state.busy = false;
    if (owners.get(date) === owner) owners.delete(date);
  }
}
function toggle(date: string): void {
  const next = new Set(expanded.value);
  if (next.has(date)) {
    next.delete(date);
    owners.get(date)?.abort();
    windows.value.delete(date);
  } else next.add(date);
  expanded.value = next;
  if (next.has(date)) void load(date);
}
function reset(): void {
  for (const owner of owners.values()) owner.abort();
  owners.clear();
  windows.value.clear();
}
watch(
  () => [props.business, props.lifecycle, props.days],
  () => {
    reset();
    expanded.value = new Set(
      [...expanded.value].filter((date) => props.days.some((item) => item.date === date))
    );
    if (!expanded.value.size && innerWidth > 820 && props.days[0])
      expanded.value.add(String(props.days[0].date));
    for (const date of expanded.value) if (visibleDates.has(date)) void load(date);
  },
  { immediate: true }
);
watch(
  () => props.visible,
  (value) => {
    if (!value) reset();
    else for (const date of expanded.value) if (visibleDates.has(date)) void load(date);
  }
);
onBeforeUnmount(() => {
  reset();
  observer.disconnect();
});
</script>

<template>
  <nexus-scroll-area class="history-index-pane" :label="t('历史日期')">
    <p v-if="loading && !days.length" role="status">{{ t('加载中') }}</p>
    <div v-else-if="error" role="alert">
      <p>{{ t(error) }}</p>
      <button @click="emit('retry')">{{ t('重试') }}</button>
    </div>
    <nexus-empty-state
      v-else-if="!days.length"
      :title="t('该时间段暂无记录')"
      :description="t('请选择其他日期范围。')"
    />
    <article
      v-for="item in days"
      :key="String(item.date)"
      :ref="(element) => track(element, String(item.date))"
      class="history-date"
      :data-date="item.date"
    >
      <button
        class="date-heading"
        :aria-expanded="expanded.has(String(item.date))"
        @click="toggle(String(item.date))"
      >
        <strong>{{ dateText(String(item.date)) }}</strong>
        <nexus-icon
          aria-hidden="true"
          :name="expanded.has(String(item.date)) ? 'chevronDown' : 'chevronRight'"
        />
      </button>
      <div v-if="expanded.has(String(item.date))" class="date-users">
        <p v-if="windows.get(String(item.date))?.error" role="alert">
          {{ windows.get(String(item.date))?.error }}
          <button @click="load(String(item.date))">{{ t('重试') }}</button>
        </p>
        <p v-else-if="!windows.has(String(item.date)) || windows.get(String(item.date))?.busy" role="status">
          {{ t('加载运行用户…') }}
        </p>
        <button
          v-for="user in windows.get(String(item.date))?.items || []"
          :key="String(user.userId)"
          class="history-user"
          :aria-pressed="selectedDate === item.date && selectedUser === (user.userId || 'null')"
          @click="emit('select', String(item.date), String(user.userId || 'null'), userTotal(user))"
        >
          <nexus-icon aria-hidden="true" name="users" />
          <strong>
            {{
              user.userId
                ? users.find((item) => item.id === user.userId)?.name || user.userId
                : t('未指定用户')
            }}
          </strong>
          <nexus-icon aria-hidden="true" name="chevronRight" />
        </button>
        <div
          v-if="(windows.get(String(item.date))?.page || 1) > 1 || windows.get(String(item.date))?.next"
          class="user-pages"
        >
          <button
            :disabled="windows.get(String(item.date))!.busy || windows.get(String(item.date))!.page <= 1"
            @click="load(String(item.date), windows.get(String(item.date))!.page - 1)"
          >
            {{ t('上一页') }}
          </button>
          <button
            :disabled="windows.get(String(item.date))!.busy || !windows.get(String(item.date))!.next"
            @click="load(String(item.date), windows.get(String(item.date))!.page + 1)"
          >
            {{ t('下一页') }}
          </button>
        </div>
      </div>
    </article>
  </nexus-scroll-area>
</template>

<style scoped src="./history-index.css"></style>
