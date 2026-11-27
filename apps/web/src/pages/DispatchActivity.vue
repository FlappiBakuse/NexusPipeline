<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, useId, watch } from 'vue';
import type { AppStore } from '../app/store';
import { registerMessages, t } from '../app/locale';
import type { ActiveRun } from '../control/execution';
import { runPhase, runPhaseTone, runProgress, runTitle } from './execution-text';
import RunningExecution from './RunningExecution.vue';

const props = defineProps<{
  store: AppStore;
  runs: ActiveRun[];
  visible: boolean;
  startStatus: string;
  cancelBlocked: boolean;
  pendingStop: Pick<ActiveRun, 'runId' | 'attemptId'> | undefined;
}>();
const emit = defineEmits<{ cancel: [run: ActiveRun] }>();
const workspace = ref<HTMLElement>();
const tablist = ref<HTMLElement>();
const tabPrefix = useId();
const selectedId = ref('');
const compareId = ref('');
const focusedId = ref('');
const wide = ref(false);
const compare = ref(false);
const review = computed(() => props.runs.filter(run => run.phase === 'reconciliationRequired'));
const runs = computed(() => [...props.runs.filter(run => run.phase !== 'reconciliationRequired'), ...review.value]);
const primary = computed(() => runs.value.find(run => run.runId === selectedId.value) ?? runs.value[0]);
const hasTabs = computed(() => runs.value.length > 1);
const canCompare = computed(() => wide.value && hasTabs.value);
const secondary = computed(() => {
  if (!canCompare.value || !compare.value) return undefined;
  return runs.value.find(run => run.runId === compareId.value && run.runId !== primary.value?.runId) ??
    runs.value.find(run => run.runId !== primary.value?.runId);
});
const panels = computed(() => [primary.value, secondary.value].filter(run => run !== undefined));

let observer: ResizeObserver | undefined;
watch(workspace, element => {
  observer?.disconnect();
  if (!element) return;
  observer = new ResizeObserver(([entry]) => {
    wide.value = (entry?.contentRect.width ?? 0) >= 720;
  });
  observer.observe(element);
});
onBeforeUnmount(() => observer?.disconnect());

watch(runs, (items, previous) => {
  if (!items.some(run => run.runId === selectedId.value)) selectedId.value = items[0]?.runId ?? '';
  if (items.length < 2) {
    compare.value = false;
    compareId.value = '';
  } else if (!items.some(run => run.runId === compareId.value)) {
    compareId.value = '';
  }
  if (focusedId.value && !items.some(run => run.runId === focusedId.value)) {
    const ownedFocus = tablist.value?.contains(document.activeElement);
    focusedId.value = '';
    if (ownedFocus && previous?.length) void nextTick(() => focusPrimary());
  }
}, { immediate: true });

function tabId(id: string): string {
  return tabPrefix + '-' + id;
}
function tabFor(id: string): HTMLElement | null {
  return tablist.value?.querySelector('#' + CSS.escape(tabId(id))) ?? null;
}
function focusPrimary(): void {
  const id = primary.value?.runId;
  if (id) (tabFor(id) ?? workspace.value?.querySelector<HTMLElement>('.dispatch-running'))?.focus({ preventScroll: true });
}
function revealTab(id: string): void {
  const list = tablist.value;
  const tab = tabFor(id);
  if (!list || !tab) return;
  const visible = list.getBoundingClientRect();
  const item = tab.getBoundingClientRect();
  if (item.left < visible.left) list.scrollLeft += item.left - visible.left;
  else if (item.right > visible.right) list.scrollLeft += item.right - visible.right;
}
watch([tablist, () => primary.value?.runId], ([list, id]) => {
  if (list && id) revealTab(id);
}, { flush: 'post' });

function select(id: string): void {
  const previous = primary.value?.runId;
  if (id === previous) return;
  if (compare.value) compareId.value = previous ?? '';
  selectedId.value = id;
}
function navigate(event: KeyboardEvent, index: number): void {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
  event.preventDefault();
  const count = runs.value.length;
  const next = event.key === 'Home' ? 0 : event.key === 'End' ? count - 1 :
    (index + (event.key === 'ArrowRight' ? 1 : -1) + count) % count;
  const id = runs.value[next]!.runId;
  tabFor(id)?.focus({ preventScroll: true });
  revealTab(id);
}
function leaveTabs(event: FocusEvent): void {
  if (!(event.relatedTarget instanceof Node) || !tablist.value?.contains(event.relatedTarget)) focusedId.value = '';
}

registerMessages({
  活动运行: 'Active runs',
  活动详情: 'Active run details',
  对照运行: 'Comparison run',
  开启双路分屏: 'Enable split view',
  已开启双路分屏: 'Split view enabled',
  右屏: 'Right pane',
  查看待核对运行: 'View runs that need review'
});
</script>

<template>
  <div ref="workspace" class="dispatch-workspace">
    <div v-if="review.length" class="dispatch-review">
      <nexus-badge tone="warning" :label="t('需要核对的运行') + ' (' + review.length + ')'" />
      <span>{{ t('请在系统诊断中查看运行收尾或配置恢复状态。') }}</span>
      <button type="button" @click="select(review[0]!.runId)">{{ t('查看待核对运行') }}</button>
    </div>
    <div v-if="primary && hasTabs" class="dispatch-controls">
      <div ref="tablist" class="dispatch-tabs" role="tablist" :aria-label="t('活动运行')" @focusout="leaveTabs">
        <button v-for="(run, index) in runs" :id="tabId(run.runId)" :key="run.runId"
          class="dispatch-tab" :class="{ 'dispatch-tab-secondary': run === secondary }"
          type="button" role="tab" :aria-selected="run === primary"
          :tabindex="run.runId === (focusedId || primary.runId) ? 0 : -1"
          :title="runTitle(run) + ' · ' + runPhase(run.phase)"
          @focus="focusedId = run.runId" @keydown="navigate($event, index)" @click="select(run.runId)">
          <span class="tab-dot" :data-tone="runPhaseTone(run.phase)" aria-hidden="true"></span>
          <span class="tab-name">{{ runTitle(run) }}</span>
          <small>{{ runProgress(run) }}%</small>
          <span v-if="run === secondary" class="tab-secondary-badge">{{ t('右屏') }}</span>
        </button>
      </div>
      <button v-if="canCompare" class="dispatch-split" type="button" :aria-pressed="!!secondary" @click="compare = !compare">
        {{ t(secondary ? '已开启双路分屏' : '开启双路分屏') }}
      </button>
    </div>
    <p v-if="primary && startStatus" class="dispatch-status" role="status">{{ startStatus }}</p>
    <div class="dispatch-panes" :data-empty="!primary" :data-split="!!secondary">
      <section v-for="run in panels" :key="run.runId + '/' + run.attemptId" class="dispatch-running pane"
        :role="hasTabs && run === primary ? 'tabpanel' : 'region'"
        :aria-labelledby="hasTabs && run === primary ? tabId(run.runId) : undefined"
        :aria-label="run === secondary ? t('对照运行') + ' · ' + runTitle(run) : !hasTabs ? t('活动详情') : undefined"
        :tabindex="run === primary ? 0 : undefined">
        <nexus-scroll-area class="dispatch-detail-scroll" direction="vertical" :label="t('活动详情')">
          <RunningExecution :store="store" :run="run" :visible="visible" :cancel-blocked="cancelBlocked"
            :cancelling="pendingStop?.runId === run.runId && pendingStop.attemptId === run.attemptId" @cancel="emit('cancel', $event)" />
        </nexus-scroll-area>
      </section>
      <section v-if="!primary" class="dispatch-running pane dispatch-idle" :aria-label="t('活动详情')">
        <strong>{{ t('运行中') }} (0)</strong>
        <div role="status">
          <nexus-empty-state class="running-empty"
            :title="startStatus || t('当前没有正在运行的任务')"
            :description="startStatus ? '' : t('选择脚本或队列后，可以在这里查看实时状态。')" />
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.dispatch-workspace {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
  min-height: 0;
}
.dispatch-controls {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.dispatch-tabs {
  display: flex;
  flex: 1;
  align-items: center;
  gap: 6px;
  min-width: 0;
  padding-bottom: 2px;
  overflow-x: auto;
}
.dispatch-tab {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 7px;
  height: 36px;
  max-width: min(270px, 88vw);
  padding: 0 11px;
  border: 1px solid var(--border);
  border-radius: var(--nx-radius-sm) var(--nx-radius-sm) var(--nx-radius-compact) var(--nx-radius-compact);
  background: var(--panel);
  color: var(--muted);
  font: inherit;
  font-size: 12px;
  font-weight: 600;
  white-space: nowrap;
  box-shadow: var(--nx-surface-shadow);
}
.dispatch-tab:hover {
  color: var(--text);
  background: var(--panel-hover);
}
.dispatch-tab[aria-selected='true'] {
  border-color: var(--accent);
  background: var(--panel-soft);
  color: var(--text);
}
.dispatch-tab-secondary {
  border-style: dashed;
  border-color: var(--ok);
  color: var(--text);
}
@media (forced-colors: active) {
  .dispatch-tab[aria-selected='true'] { border-color: Highlight; text-decoration: underline; }
}
.dispatch-split[aria-pressed='true'] {
  border-color: var(--accent);
  background: var(--accent-soft);
  color: var(--text);
}
.tab-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.tab-dot {
  flex: none;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  --tab-dot-color: var(--accent);
  background: var(--tab-dot-color);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--tab-dot-color) 15%, transparent);
}
.tab-dot[data-tone='warning'] { --tab-dot-color: var(--warn); }
.tab-dot[data-tone='danger'] { --tab-dot-color: var(--bad); }
@media (prefers-reduced-motion: no-preference) {
  .tab-dot[data-tone='info'] { animation: tab-pulse 1.8s ease-in-out infinite; }
}
@keyframes tab-pulse {
  50% { opacity: 0.55; transform: scale(0.88); }
}
.tab-secondary-badge {
  flex: none;
  display: inline-flex;
  align-items: center;
  height: 18px;
  padding-inline: 5px;
  border: 1px solid color-mix(in srgb, var(--ok) 28%, transparent);
  border-radius: var(--nx-radius-compact);
  background: color-mix(in srgb, var(--ok) 14%, transparent);
  color: var(--ok);
  font-size: 10px;
  font-weight: 650;
}
.dispatch-tab small { color: var(--muted); font-size: 11px; }
.dispatch-status { color: var(--muted); font-size: 12px; }
.dispatch-status { margin: 0; }
.dispatch-split { flex: none; }
.dispatch-panes {
  display: grid;
  flex: 1;
  grid-template-columns: minmax(0, 1fr);
  grid-template-rows: minmax(0, 1fr);
  gap: 12px;
  min-height: 0;
}
.dispatch-panes[data-empty='true'] {
  flex: none;
  grid-template-rows: auto;
}
.dispatch-panes[data-split='true'] { grid-template-columns: repeat(2, minmax(0, 1fr)); }
.dispatch-running {
  display: flex;
  flex-direction: column;
  min-height: 0;
  padding: 12px;
}
.dispatch-idle {
  gap: 12px;
  padding: 20px;
}
.dispatch-idle .running-empty {
  border-radius: var(--nx-radius-md);
}
.dispatch-idle > strong {
  font-size: 15px;
  line-height: 1.35;
}
.dispatch-detail-scroll {
  flex: 1;
  min-height: 0;
}
.dispatch-review {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border: 1px solid color-mix(in srgb, var(--warn) 38%, var(--border));
  border-radius: var(--nx-radius-md);
  background: color-mix(in srgb, var(--warn) 10%, var(--panel));
  font-size: 12px;
}
.dispatch-review > span { flex: 1 1 240px; }
.dispatch-review button { min-height: 28px; }
</style>
