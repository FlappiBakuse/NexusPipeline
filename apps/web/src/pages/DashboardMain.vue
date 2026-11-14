<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { arrayValue, objectValue } from '../control/client';
import type { SurfaceContribution } from '@nexuspipeline/extension-sdk';
import type { DashboardCard } from '../control/interface';
import ModalTransition from '../components/ModalTransition.vue';
import { useDashboardLayout } from './dashboard-layout';
import DashboardRunningCard from './DashboardRunningCard.vue';
import CompletionNotice from './CompletionNotice.vue';
import DashboardHistoryChart from './DashboardHistoryChart.vue';
import DashboardLayout from './DashboardLayout.vue';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import { locale, t } from '../app/locale';
import './dashboard-messages';
import type { ActiveRun, RecordSummary } from './dashboard-types';
import { executionRun } from '../control/execution';
import { sevenDays, dashboardSummary } from './dashboard-summary';

const props = defineProps<{ store: AppStore; visible: boolean; overviewCards?: readonly { extensionId: string; id: string; dashboard?: SurfaceContribution['dashboard'] }[] }>();
const catalog = computed<{ id: DashboardCard; title: string; description: string }[]>(() => [
  ...(props.overviewCards ?? []).flatMap(item => item.dashboard ? [{ id: item.dashboard.key, title: item.dashboard.title, description: item.dashboard.description }] : []),
  {
    id: 'status',
    title: locale.value === 'en-US' ? 'System status' : '一切准备就绪',
    description: '显示系统空闲或任务进行中的状态，并汇总当前活动任务数量。'
  },
  {
    id: 'running',
    title: '正在运行',
    description: '查看正在执行的脚本和队列，以及当前步骤、重试进度和运行状态。'
  },
  {
    id: 'history-duration',
    title: '运行历史统计',
    description: '汇总最近七天的运行时长及总次数、成功次数和异常次数；两张图表作为一个整体排序、显示或隐藏。'
  }
]);
const {
  layout,
  layoutCards,
  layoutOpen,
  layoutBusy,
  layoutError,
  layoutNotice,
  cards,
  readLayout,
  beginLayout,
  cancelLayout,
  closeLayout,
  saveLayout,
  changeLayout,
  resetLayout
} = useDashboardLayout(props.store, () => (props.overviewCards ?? []).flatMap(item => item.dashboard && item.dashboard.defaultVisible ? [item.dashboard] : []).sort((a, b) => a.order - b.order).map(item => item.key));
const statisticsVisible = computed(() => cards.value.includes('history-duration'));

const active = ref<ActiveRun[]>([]);
const activeLoading = ref(false);
const activeError = ref('');
let activeOwner: AbortController | undefined;
let activeAgain = false;
async function readActive(): Promise<void> {
  if (!props.visible) return;
  if (activeOwner) {
    activeAgain = true;
    return;
  }
  const owner = new AbortController();
  activeOwner = owner;
  activeLoading.value = true;
  try {
    const row = await props.store.client.read('/control/v1/execution/active', objectValue, owner.signal);
    owner.signal.throwIfAborted();
    active.value = arrayValue(row.items, executionRun);
    activeError.value = '';
  } catch (reason) {
    if (!owner.signal.aborted) {
      props.store.report(reason);
      activeError.value = reason instanceof Error ? reason.message : '运行状态不可用。';
    }
  } finally {
    if (activeOwner === owner) activeOwner = undefined;
    activeLoading.value = false;
    if (activeAgain && props.visible) {
      activeAgain = false;
      void readActive();
    }
  }
}

const summary = ref<RecordSummary | null>(null);
const summaryLoading = ref(false);
const summaryError = ref('');
const dates = ref<string[]>([]);
let summaryOwner: AbortController | undefined;
let summaryAgain = false;
async function readSummary(): Promise<void> {
  if (!props.visible || !statisticsVisible.value || !props.store.can('records.read')) return;
  if (summaryOwner) {
    summaryAgain = true;
    return;
  }
  const owner = new AbortController();
  summaryOwner = owner;
  summaryLoading.value = true;
  const range = sevenDays();
  dates.value = range.dates;
  try {
    const row = await props.store.client.read(
      '/control/v1/records/summary/days?' + range.query,
      objectValue,
      owner.signal
    );
    owner.signal.throwIfAborted();
    summary.value = dashboardSummary(row);
    summaryError.value = '';
  } catch (reason) {
    if (!owner.signal.aborted) {
      props.store.report(reason);
      summaryError.value = reason instanceof Error ? reason.message : '历史统计不可用。';
    }
  } finally {
    if (summaryOwner === owner) summaryOwner = undefined;
    summaryLoading.value = false;
    if (summaryAgain && props.visible) {
      summaryAgain = false;
      void readSummary();
    }
  }
}

let layoutTimer: ReturnType<typeof setInterval> | undefined;
let dayTimer: ReturnType<typeof setTimeout> | undefined;
function armDayBoundary(): void {
  if (dayTimer) clearTimeout(dayTimer);
  const current = new Date();
  const next = new Date(current.getFullYear(), current.getMonth(), current.getDate() + 1);
  dayTimer = setTimeout(
    () => {
      void readSummary();
      armDayBoundary();
    },
    Math.max(1000, next.getTime() - current.getTime() + 1000)
  );
}
watch(
  [() => props.visible, () => props.store.state.preparationRevision],
  ([visible]) => {
    if (visible) {
      void readActive();
      if (!layout.value) void readLayout();
      void readSummary();
    } else {
      activeOwner?.abort();
      summaryOwner?.abort();
    }
  },
  { immediate: true }
);
watch(
  () => props.store.state.events?.executionRevision,
  () => {
    if (props.visible) void readActive();
  }
);
watch(
  () => props.store.state.events?.completedRuns,
  (next, previous) => {
    if (next !== previous && summary.value && props.visible) void readSummary();
  }
);
watch(statisticsVisible, (visible) => {
  if (visible && props.visible) void readSummary();
});
onMounted(() => {
  layoutTimer = setInterval(() => {
    if (props.visible && !layoutCards.value) void readLayout();
  }, 60000);
  armDayBoundary();
});
onBeforeUnmount(() => {
  activeOwner?.abort();
  summaryOwner?.abort();
  if (layoutTimer) clearInterval(layoutTimer);
  if (dayTimer) clearTimeout(dayTimer);
});
</script>

<template>
  <div class="dashboard">
    <PageActions :visible="visible">
      <button
        class="primary"
        :disabled="
          layoutBusy ||
          !layout ||
          !store.can('settings.write')
        "
        @click="beginLayout"
      >
        {{ t('编辑页面布局') }}
      </button>
      <button v-if="layoutError && !layoutCards" @click="readLayout">{{ t('重新读取') }}</button>
    </PageActions>
    <nexus-toast v-if="layoutError" tone="danger" :message.prop="t(layoutError)" />
    <nexus-toast v-if="layoutNotice" :message.prop="t(layoutNotice)" @nexus-close="layoutNotice = ''" />
    <CompletionNotice :store="store" :visible="visible" />
    <p v-if="!layout && layoutBusy" role="status">{{ t('正在读取卡片布局…') }}</p>
    <div v-if="layout" class="dashboard-grid">
      <template v-for="card in cards.filter(item => catalog.some(card => card.id === item))" :key="card">
        <section
          v-if="card === 'status'"
          class="overview full-card"
          :class="{ active: active.length }"
          aria-live="polite"
        >
          <span class="status-label">
            <i aria-hidden="true"></i>
            {{ t(active.length ? '运行中' : '系统空闲') }}
          </span>
          <strong>{{ t(active.length ? '任务正在执行' : '一切准备就绪') }}</strong>
          <p v-if="active.length">
            {{ t('当前有') }} {{ active.length }} {{ t('个活动任务，状态会自动更新。') }}
          </p>
          <p v-else>{{ t('当前没有活动任务，可以从调度中心手动执行脚本或队列。') }}</p>
        </section>
        <section v-else-if="card === 'running'" class="running-surface full-card">
          <DashboardRunningCard
            :items="active"
            :bundle="store.state.automation?.bundle"
            :loading="activeLoading"
            :error="activeError"
            @details="store.navigate('dispatch')"
          />
          <ExtensionOutlet target="overview.after-running" :store="store" :visible="visible" />
        </section>
        <ExtensionOutlet v-else-if="overviewCards?.some(item => item.dashboard?.key === card)" class="full-card"
          target="overview.cards" :entry="overviewCards!.find(item => item.dashboard?.key === card)!" :store="store" :visible="visible" />
        <DashboardHistoryChart
          v-else
          class="full-card"
          :summary="summary"
          :loading="summaryLoading"
          :error="summaryError"
          :dates="dates"
        />
      </template>
    </div>
    <ModalTransition>
      <nexus-modal
        v-if="layoutOpen"
        :open.prop="visible"
        :title="t('编辑页面布局')"
        size="wide"
        surface="secondary"
        @nexus-close="closeLayout"
      >
        <p v-if="layoutError" role="alert">{{ t(layoutError) }}</p>
        <DashboardLayout
          v-if="layoutCards"
          :cards="layoutCards"
          :catalog="catalog"
          :locked="layoutBusy"
          @change="changeLayout"
        />
        <div slot="footer" class="layout-footer">
          <button :disabled="layoutBusy" @click="resetLayout">
            {{ t('恢复默认') }}
          </button>
          <button :disabled="layoutBusy" @click="cancelLayout">{{ t('取消') }}</button>
          <button
            class="primary"
            :disabled="layoutBusy || !store.can('settings.write')"
            @click="saveLayout"
          >
            {{ t('保存') }}
          </button>
        </div>
      </nexus-modal>
    </ModalTransition>
  </div>
</template>

<style scoped src="./dashboard-main.css"></style>
