<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import { attemptTitle } from './history-format';
import { readHistoryLog } from './history-log';
import VirtualHistoryList from '../components/VirtualHistoryList.vue';
import { t } from '../app/locale';
import ScriptLogPanel from '../components/ScriptLogPanel.vue';
import { needsLogSeparator, scriptLogRoleLabels, scriptLogRoles, type ScriptLogSelection } from '../components/script-log-roles';
import type { HistoryLog } from './attempt-resources';
const props = withDefaults(
  defineProps<{
    store: RecordsStore;
    runId: string;
    attemptId: string;
    visible: boolean;
    ordinal?: number;
    logs: HistoryLog[];
  }>(),
  { ordinal: 1 }
);
const disclosure = ref<'closed' | 'opening' | 'open'>('closed');
const trigger = ref<HTMLButtonElement>();
const content = ref<HTMLElement>();
const panel = ref<InstanceType<typeof ScriptLogPanel>>();
const text = shallowRef<readonly string[]>([]);
const error = ref('');
const busy = ref(false);
const selectedId = ref('');
const roleSelection = ref<ScriptLogSelection>('all');
const title = computed(() => t('脚本日志') + '（' + attemptTitle(props.ordinal) + '）');
type LogGroup = { id: string; stepId: string; ordinal: number | undefined; sources: Map<string, HistoryLog> };
const groups = computed(() => {
  const indexed = new Map<string, LogGroup>();
  for (const log of props.logs) {
    const { stepId, invocationOrdinal, role } = log.key;
    const id = JSON.stringify([stepId, invocationOrdinal ?? null]);
    let group = indexed.get(id);
    if (!group) {
      group = { id, stepId, ordinal: invocationOrdinal, sources: new Map() };
      indexed.set(id, group);
    }
    group.sources.set(role, log);
  }
  return [...indexed.values()];
});
const selected = computed(() => groups.value.find(group => group.id === selectedId.value) ?? groups.value[0]);
const roles = computed(() => scriptLogRoles.filter(role => selected.value?.sources.has(role)));
const selectedRole = computed(() => roleSelection.value !== 'all' && roles.value.includes(roleSelection.value)
  ? roleSelection.value : 'all');
const sources = computed(() => {
  const group = selected.value;
  if (!group) return [];
  const requested = selectedRole.value === 'all' ? roles.value : [selectedRole.value];
  return requested.map(role => group.sources.get(role)!);
});
const request = computed(() => props.visible && disclosure.value !== 'closed' && sources.value.length
  ? JSON.stringify([props.runId, props.attemptId, selected.value?.id, sources.value.map(log => [log.key.role, log.snapshotSha256])])
  : '');
const groupId = (group: LogGroup) => group.id;
function selectGroup(id: string): void {
  selectedId.value = id;
  roleSelection.value = 'all';
}
let owner: AbortController | undefined;
function showLog(): void {
  const moveFocus = trigger.value === document.activeElement;
  disclosure.value = 'open';
  if (moveFocus) void nextTick(() => content.value?.focus({ preventScroll: true }));
}
function expand(): void {
  if (disclosure.value !== 'closed') return;
  if (sources.value.length) disclosure.value = 'opening';
  else showLog();
}
function conceal(panel: Element): void {
  (panel as HTMLElement).inert = true;
}
async function load(): Promise<void> {
  owner?.abort();
  owner = undefined;
  text.value = [];
  error.value = '';
  const logs = sources.value;
  const fingerprint = request.value;
  busy.value = !!fingerprint;
  if (!fingerprint) {
    if (disclosure.value === 'opening') showLog();
    return;
  }
  const controller = new AbortController();
  owner = controller;
  try {
    const transcripts = await Promise.allSettled(logs.map(log =>
      readHistoryLog(props.store.client, props.runId, props.attemptId, log, controller.signal)));
    if (controller.signal.aborted || request.value !== fingerprint) return;
    const output: string[] = [], failures: string[] = [];
    for (const [index, transcript] of transcripts.entries()) {
      if (transcript.status === 'rejected') {
        const role = logs[index]!.key.role as keyof typeof scriptLogRoleLabels;
        const reason = transcript.reason;
        failures.push(t(scriptLogRoleLabels[role]) + ' · ' + t(reason instanceof Error ? reason.message : String(reason)));
        continue;
      }
      const segments = transcript.value.filter(Boolean);
      if (!segments.length) continue;
      if (needsLogSeparator(output.at(-1) ?? '', segments[0]!)) output.push('\n');
      output.push(...segments);
    }
    error.value = failures.join('\n');
    text.value = output;
    if (panel.value?.viewport) panel.value.viewport.scrollTop = 0;
  } catch (reason) {
    if (!controller.signal.aborted && request.value === fingerprint)
      error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (owner === controller) {
      owner = undefined;
      busy.value = false;
      if (!controller.signal.aborted && request.value === fingerprint && disclosure.value === 'opening') showLog();
    }
  }
}
watch([() => props.runId, () => props.attemptId], () => {
  disclosure.value = 'closed';
  selectedId.value = '';
  roleSelection.value = 'all';
});
watch(request, load);
onBeforeUnmount(() => owner?.abort());
</script>

<template>
  <div class="history-log-disclosure">
    <button v-if="disclosure !== 'open'" ref="trigger" type="button" class="history-log-toggle"
      :aria-disabled="disclosure === 'opening'" @click="expand">
      <nexus-spinner v-if="disclosure === 'opening'" :label="t('正在读取脚本日志…')" />
      <template v-else>
        <strong>{{ title }}</strong>
        <span>{{ t('点击展开') }}</span>
      </template>
    </button>
    <Transition :key="runId + '/' + attemptId" name="disclosure" @before-leave="conceal">
      <div v-if="visible && disclosure === 'open'" class="disclosure-reveal">
        <div class="disclosure-clip">
          <div ref="content" class="history-log-content" role="region" :aria-label="title" :aria-busy="busy" tabindex="-1">
            <nav v-if="groups.length > 1" :aria-label="t('脚本日志')">
              <VirtualHistoryList :items="groups" :item-key="groupId" :label="t('脚本日志')" :visible="visible">
                <template #default="{ item: group }">
                  <button type="button" :aria-pressed="selected?.id === group.id" @click="selectGroup(group.id)">
                    {{ group.stepId }}<template v-if="group.ordinal"> · {{ attemptTitle(group.ordinal) }}</template>
                  </button>
                </template>
              </VirtualHistoryList>
            </nav>
            <ScriptLogPanel ref="panel" :title="t('脚本日志')" :roles="roles" :selected="selectedRole"
              @select="roleSelection = $event">
              <nexus-spinner v-if="busy" :label="t('正在读取脚本日志…')" />
              <template v-else>
                <template v-if="error">
                  <nexus-toast tone="danger" :message.prop="error" />
                  <button type="button" @click="load">{{ t('重试') }}</button>
                </template>
                <pre v-if="text.length || !error"><template v-if="text.length"><template v-for="(segment, index) in text" :key="index">{{ segment }}</template></template><template v-else>{{ t('没有可用的日志') }}</template></pre>
              </template>
            </ScriptLogPanel>
          </div>
        </div>
      </div>
    </Transition>
  </div>
</template>
<style scoped>
.history-log-disclosure {
  min-width: 0;
}
.history-log-toggle {
  box-sizing: border-box;
  width: 100%;
  min-height: 76px;
  padding: 14px;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  background: transparent;
  border: 1px dashed var(--nx-color-border);
  border-radius: var(--nx-radius-sm);
  cursor: pointer;
  box-shadow: var(--nx-surface-shadow);
}
.history-log-toggle strong {
  font-size: 12px;
  font-weight: 400;
  color: var(--text);
}
.history-log-toggle span {
  color: var(--muted);
  font-size: 12px;
  font-weight: 400;
}
.history-log-toggle[aria-disabled='true'] {
  cursor: progress;
  opacity: 1;
}
</style>
