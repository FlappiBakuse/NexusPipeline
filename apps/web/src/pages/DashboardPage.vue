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
import { executionRun, type ActiveRun } from '../control/execution';
import { sevenDays, dashboardSummary, type RecordSummary } from './dashboard-summary';

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
  layoutAvailableCards,
  layoutOpen,
  layoutBusy,
  layoutError,
  layoutNotice,
  cards,
  readLayout,
  beginLayout,
  cancelLayout,
  saveLayout,
  changeLayout,
  changeAvailableLayout,
  resetLayout
} = useDashboardLayout(props.store, () => (props.overviewCards ?? []).flatMap(item => item.dashboard && item.dashboard.defaultVisible ? [item.dashboard] : []).sort((a, b) => a.order - b.order).map(item => item.key), () => catalog.value.map(item => item.id));
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
    <nexus-toast v-if="visible && layoutError" tone="danger" :message.prop="t(layoutError)" />
    <nexus-toast v-if="visible && statisticsVisible && summaryError" tone="danger" :message.prop="t(summaryError)" />
    <nexus-toast v-if="layoutNotice" :message.prop="t(layoutNotice)" @nexus-close="layoutNotice = ''" />
    <CompletionNotice :store="store" :visible="visible" />
    <nexus-spinner v-if="!layout && layoutBusy" :label="t('正在读取卡片布局…')" />
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
          <strong>{{ active.length ? t('任务正在执行') : t('当前没有正在运行的任务') }}</strong>
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
        @nexus-close="cancelLayout"
      >
        <DashboardLayout
          v-if="layoutCards"
          :cards="layoutCards"
          :available="layoutAvailableCards"
          :catalog="catalog"
          :locked="layoutBusy"
          @change="changeLayout"
          @reorder-available="changeAvailableLayout"
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

<style scoped>
.layout-footer {
  display: flex;
  justify-content: end;
  gap: 12px;
  flex-wrap: wrap;
}
.layout-footer > button { min-width: 80px; }
.dashboard {
  display: grid;
  gap: 16px;
}
.dashboard-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
}
.full-card {
  grid-column: 1 / -1;
}
.overview {
  padding: 18px 22px;
  border: 1px solid var(--border);
  border-radius: 16px;
  background: var(--panel);
}
.overview strong {
  display: block;
  margin: 6px 0 4px;
  font-size: 19px;
  line-height: 1.35;
}
.status-label {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--muted);
}
.status-label i {
  width: 7px;
  height: 7px;
  background: var(--muted);
  border-radius: 50%;
}
.overview.active .status-label {
  color: var(--accent);
}
.overview.active .status-label i {
  background: var(--accent);
  box-shadow: 0 0 0 4px var(--accent-soft);
}
.overview p {
  margin: 0;
  color: var(--muted);
  font-size: 13px;
  line-height: 1.65;
}
.running-surface {
  min-width: 0;
  padding: 20px;
  border: 1px solid var(--border);
  border-radius: 16px;
  background: var(--panel);
}
@media (max-width: 1023px) {
  .dashboard-grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
