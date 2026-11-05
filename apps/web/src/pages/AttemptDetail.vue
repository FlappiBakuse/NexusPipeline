<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { historyImage, type HistoryImage, type AttemptFacts, type AttemptTiming } from '../control/records';
import { arrayValue, integerValue, objectValue, stringValue } from '../control/client';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import HistoryInvocations from './HistoryInvocations.vue';
import { executionRun, type ActiveStep } from '../control/execution';
import AttemptLogs from './AttemptLogs.vue';
import AttemptImages from './AttemptImages.vue';
import { attemptTitle, historyDuration, historyTime, historyReason } from './history-detail';
import { historyRow } from './history-row';
import { t } from '../app/locale';
import './attempt-messages';

const props = withDefaults(
  defineProps<{
    store: AppStore;
    runId: string;
    attemptId: string;
    visible: boolean;
    ordinal?: number;
    facts: AttemptFacts;
    timing?: AttemptTiming;
    stepIds?: string[];
    stepNames?: { stepId: string; scriptName: string }[];
    userId?: string;
  }>(),
  { ordinal: 1 }
);
const emit = defineEmits<{ focus: [] }>();
type LogItem = Record<string, unknown> & { key: { stepId: string; role: string; invocationOrdinal?: number } };
const root = ref<HTMLElement>();
const shown = ref(false);
const logs = ref<LogItem[]>([]);
const images = ref<HistoryImage[]>([]);
const steps = ref<ActiveStep[]>([]);
const scopedFacts = ref<AttemptFacts>();
const error = ref('');
const base = computed(() => '/control/v1/records/' + props.runId + '/attempts/' + props.attemptId);
const summary = computed(() => historyRow({ runId: props.runId, finalAttempt: scopedFacts.value || props.facts }));
const active = computed(() => props.visible && shown.value);
const hasInvocations = computed(() => steps.value.some(step => step.invocations?.length));
let observer: IntersectionObserver | undefined;
let owner: AbortController | undefined;
let loaded = false;
watch(root, (element) => {
  observer?.disconnect();
  if (!element) return;
  observer = new IntersectionObserver(
    (entries) => {
      shown.value = entries[0]?.isIntersecting === true;
    },
    { rootMargin: '240px' }
  );
  observer.observe(element);
});
async function catalog(kind: string, signal: AbortSignal): Promise<Record<string, unknown>[]> {
  const items: Record<string, unknown>[] = [];
  let after = -1;
  for (;;) {
    const result = await props.store.client.read(
      base.value + '/' + kind + '?limit=100&afterPosition=' + after,
      objectValue,
      signal
    );
    signal.throwIfAborted();
    const page = arrayValue(result.items, objectValue);
    if (page.length > 100 || items.length + page.length > 4096) throw Error('history_catalog_limit_invalid');
    items.push(...page.filter(item => !props.stepIds || props.stepIds.includes(stringValue(objectValue(item.key).stepId))));
    if (result.nextPosition == null) return items;
    const next = integerValue(result.nextPosition);
    if (next <= after || next > 4095) throw Error('history_catalog_cursor_invalid');
    after = next;
  }
}
async function load(): Promise<void> {
  if (!active.value || loaded) return;
  owner?.abort();
  const controller = new AbortController();
  owner = controller;
  error.value = '';
  try {
    const result = await Promise.allSettled([
      props.store.can('records.logs.read') ? catalog('logs', controller.signal) : [],
      props.store.can('records.images.read') ? catalog('images', controller.signal) : [],
      props.store.client.read(base.value + '/report', executionRun, controller.signal)
    ]);
    controller.signal.throwIfAborted();
    if (result[0].status === 'fulfilled')
      logs.value = result[0].value.map((item) => {
        const key = objectValue(item.key);
        return { ...item, key: { stepId: stringValue(key.stepId), role: stringValue(key.role) } };
      });
    if (result[1].status === 'fulfilled') images.value = result[1].value.map(value => historyImage(value, props.runId, props.attemptId));
    if (result[2].status === 'fulfilled') {
      steps.value = result[2].value.steps.filter(step => !props.stepIds || props.stepIds.includes(step.stepId));
      scopedFacts.value = result[2].value.accounts?.find(account => account.userId === props.userId)?.facts;
    }
    loaded = result.every((item) => item.status === 'fulfilled');
    const failures = result.filter((item): item is PromiseRejectedResult => item.status === 'rejected');
    error.value = failures
      .map((item) => (item.reason instanceof Error ? item.reason.message : String(item.reason)))
      .join(' · ');
  } catch (reason) {
    if (!controller.signal.aborted) error.value = reason instanceof Error ? reason.message : String(reason);
  }
}
watch(
  active,
  (value) => {
    if (value) void load();
    else owner?.abort();
  },
  { immediate: true }
);
watch(
  () => [props.runId, props.attemptId, props.stepIds] as const,
  () => {
    owner?.abort();
    loaded = false;
    logs.value = [];
    images.value = [];
    steps.value = [];
    scopedFacts.value = undefined;
    if (active.value) void load();
  }
);
onBeforeUnmount(() => {
  observer?.disconnect();
  owner?.abort();
});
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
        <dd>{{ historyReason((scopedFacts || facts).reasonCode) }}</dd>
      </div>
    </dl>
    <p v-if="error" role="alert">
      {{ t(error) }}
      <button @click="load">{{ t('重试') }}</button>
    </p>
    <HistoryInvocations v-if="hasInvocations" :store="store" :run-id="runId"
      :attempt-id="attemptId" :visible="active" :steps="steps" :step-names="stepNames" :logs="logs" :images="images" />
    <AttemptLogs v-else
      :store="store"
      :run-id="runId"
      :attempt-id="attemptId"
      :visible="active"
      :logs="logs"
      :ordinal="ordinal"
    />
    <AttemptImages v-if="!hasInvocations"
      :store="store"
      :run-id="runId"
      :attempt-id="attemptId"
      :visible="visible"
      :images="images"
    />
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
