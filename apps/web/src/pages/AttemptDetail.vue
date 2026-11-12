<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import type { AttemptFacts, AttemptTiming } from '../control/records';
import type { ActiveRun } from '../control/execution';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import HistoryInvocations from './HistoryInvocations.vue';
import AttemptLogs from './AttemptLogs.vue';
import AttemptImages from './AttemptImages.vue';
import { attemptTitle, historyDuration, historyTime, historyReason } from './history-detail';
import { historyRow } from './history-row';
import { t } from '../app/locale';
import { useAttemptResources } from './attempt-resources';
import './attempt-messages';

const props = withDefaults(
  defineProps<{
    store: RecordsStore;
    runId: string;
    attemptId: string;
    visible: boolean;
    ordinal?: number;
    facts: AttemptFacts;
    timing?: AttemptTiming;
    stepIds?: string[];
    stepNames?: { stepId: string; scriptName: string }[];
    userId?: string;
    preparedReport?: ActiveRun;
  }>(),
  { ordinal: 1 }
);
const emit = defineEmits<{ focus: [] }>();
const root = ref<HTMLElement>();
const shown = ref(false);
const active = computed(() => props.visible && shown.value);
const { state, load } = useAttemptResources(props, active);
const summary = computed(() => historyRow({ runId: props.runId, finalAttempt: state.report.facts || props.facts }));
const hasInvocations = computed(() => state.report.steps.some(step => step.invocations?.length));
const reportReady = computed(() => state.report.loaded || !!state.report.error);
const indexing = computed(() => state.logs.indexing || state.images.indexing || state.report.indexing);
let observer: IntersectionObserver | undefined;
watch(root, (element) => {
  observer?.disconnect();
  if (!element) return;
  observer = new IntersectionObserver(
    (entries) => {
      shown.value = entries[0]?.isIntersecting === true;
    },
    { rootMargin: '0px' }
  );
  observer.observe(element);
});
onBeforeUnmount(() => observer?.disconnect());
</script>

<template>
  <section ref="root" class="attempt-detail" :class="{ 'history-attempt-card': !hasInvocations }" @focusin="emit('focus')">
    <header v-if="!hasInvocations">
      <h3>{{ attemptTitle(ordinal) }}</h3>
      <nexus-badge :tone="summary.tone">{{ summary.label }}</nexus-badge>
    </header>
    <dl v-if="!hasInvocations" class="history-detail-metadata">
      <div>
        <dt>{{ t('时间') }}</dt>
        <dd>{{ historyTime(timing?.startedAt) }} - {{ historyTime(timing?.endedAt) }}</dd>
      </div>
      <div>
        <dt>{{ t('耗时') }}</dt>
        <dd>{{ historyDuration(timing?.durationMilliseconds) }}</dd>
      </div>
      <div class="history-attempt-reason">
        <dt>{{ t('原因') }}</dt>
        <dd>{{ historyReason((state.report.facts || facts).reasonCode) }}</dd>
      </div>
    </dl>
    <p v-if="indexing" role="status">{{ t('历史索引正在补齐') }}</p>
    <p v-for="kind in (['report', 'logs', 'images'] as const)" v-show="state[kind].error" :key="kind" role="alert">
      {{ t(kind === 'report' ? '运行详情' : kind === 'logs' ? '脚本日志' : '运行截图') }}: {{ t(state[kind].error) }}
      <button @click="load(kind)">{{ t('重试') }}</button>
    </p>
    <HistoryInvocations v-if="hasInvocations" :store="store" :run-id="runId"
      :attempt-id="attemptId" :visible="active" :steps="state.report.steps" :step-names="stepNames"
      :logs="state.logs.items" :images="state.images.items" :logs-ready="state.logs.loaded" :images-ready="state.images.loaded" />
    <AttemptLogs v-else-if="reportReady && state.logs.loaded"
      :store="store"
      :run-id="runId"
      :attempt-id="attemptId"
      :visible="active"
      :logs="state.logs.items"
      :ordinal="ordinal"
    />
    <AttemptImages v-if="reportReady && !hasInvocations && state.images.loaded"
      :store="store"
      :run-id="runId"
      :attempt-id="attemptId"
      :visible="active"
      :images="state.images.items"
    />
    <button v-if="state.logs.loaded && state.logs.next !== null" :disabled="!active || state.logs.busy" @click="load('logs')">
      {{ t('加载更多日志') }}
    </button>
    <button v-if="state.images.loaded && state.images.next !== null" :disabled="!active || state.images.busy" @click="load('images')">
      {{ t('加载更多截图') }}
    </button>
    <ExtensionOutlet
      :store="store"
      target="records.detail.sections"
      :entity-id="attemptId"
      :run-id="runId"
      :attempt-id="attemptId"
      :visible="active"
    />
  </section>
</template>
