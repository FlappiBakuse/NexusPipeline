<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { integerValue, objectValue, stringValue } from '../control/client';
import { t } from '../app/locale';
import { diagnosticCopy } from './diagnostic-copy';

const props = defineProps<{ store: AppStore; visible: boolean }>();
interface Report {
  admission: string;
  isolated: number;
  held: number;
  pending: number;
  framework: string;
  architecture: string;
  extensions: number;
  capturedAt: string;
  checks: { id: string; status: string; code: string; count: number | null }[];
}
const report = ref<Report | null>(null);
const busy = ref(false);
const exporting = ref(false);
const error = ref('');
let owner: AbortController | undefined;
const groups = computed(() => !report.value ? [] : [
  { title: '运行环境', ids: ['host.', 'web.', 'mcp.', 'dependency.'] },
  { title: '数据与恢复', ids: ['recovery.', 'update.', 'config.', 'execution.', 'scheduler.', 'logs.'] },
  { title: '插件状态', ids: ['plugin.'] }
].map(group => ({ title: group.title, rows: report.value!.checks
  .filter(check => group.ids.some(prefix => check.id.startsWith(prefix)))
  .map(check => ({ title: diagnosticCopy(check.id), ready: ['pass', 'notRequired'].includes(check.status),
    status: diagnosticCopy(check.status),
    detail: diagnosticCopy(check.code) + (check.count === null ? '' : ' · ' + check.count) })) })));
const count = computed(() => groups.value.reduce((total, group) => total + group.rows.length, 0));
const attention = computed(() =>
  groups.value.reduce((total, group) => total + group.rows.filter((row) => !row.ready).length, 0)
);
watch(
  () => props.visible,
  (visible) => {
    if (!visible) owner?.abort();
    else if (!report.value) void read();
  },
  { immediate: true }
);
onBeforeUnmount(() => owner?.abort());

async function read(): Promise<void> {
  if (!props.visible || busy.value || !props.store.can('control.doctor.read')) return;
  const current = new AbortController();
  owner = current;
  busy.value = true;
  error.value = '';
  try {
    const value = await props.store.client.read('/control/v1/doctor', objectValue, current.signal);
    const details = objectValue(value.details);
    const next = {
      admission: stringValue(value.admission),
      isolated: integerValue(value.isolatedResourceCount),
      held: integerValue(value.heldResourceCount),
      pending: integerValue(value.pendingOperationCount),
      framework: stringValue(details.frameworkVersion),
      architecture: stringValue(details.processArchitecture),
      extensions: integerValue(details.installedExtensionCount),
      capturedAt: stringValue(value.capturedAtUtc),
      checks: readChecks(details.checks)
    };
    current.signal.throwIfAborted();
    report.value = next;
  } catch (reason) {
    if (!current.signal.aborted) {
      props.store.report(reason);
      error.value = '诊断读取失败，请重试。';
    }
  } finally {
    if (owner === current) {
      owner = undefined;
      busy.value = false;
    }
  }
}
function readChecks(value: unknown): Report['checks'] {
  if (!Array.isArray(value) || value.length < 1 || value.length > 32) throw new Error('diagnostic_checks_invalid');
  return value.map(item => {
    const check = objectValue(item);
    const id = stringValue(check.id), status = stringValue(check.status), code = stringValue(check.code);
    diagnosticCopy(id); diagnosticCopy(status); diagnosticCopy(code);
    return { id, status, code, count: check.count === null ? null : integerValue(check.count) };
  });
}
async function support(): Promise<void> {
  if (exporting.value || !props.store.can('control.doctor.read')) return;
  exporting.value = true;
  error.value = '';
  try {
    const response = await props.store.client.response('/control/v1/support');
    const url = URL.createObjectURL(await response.blob());
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = 'nexus-support.zip';
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 30000);
  } catch (reason) {
    props.store.report(reason);
    error.value = '诊断导出失败，请重试。';
  } finally {
    exporting.value = false;
  }
}
</script>

<template>
  <div class="diagnostics" :aria-busy="busy">
    <p v-if="error" role="alert">{{ t(error) }}</p>
    <p v-if="!report && busy" role="status">{{ t('诊断加载中…') }}</p>
    <template v-if="report">
      <div class="overview">
        <div>
          <strong>{{ t('总体状态') }}</strong>
          <p>
            {{ count }} {{ t('项检查：') }}{{ t(attention ? '存在需要处理的项目' : '全部通过或按条件跳过') }}
          </p>
        </div>
        <nexus-badge :tone="attention ? 'warning' : 'success'" :label="t(attention ? '需关注' : '正常')" />
      </div>
      <section v-for="(group, index) in groups" :key="group.title" class="group">
        <header>
          <span class="number">{{ index + 1 }}</span>
          <strong>{{ t(group.title) }}</strong>
          <nexus-badge
            :tone="group.rows.every((row) => row.ready) ? 'success' : 'warning'"
            :label="t(group.rows.every((row) => row.ready) ? '正常' : '需关注')"
          />
        </header>
        <div class="row table-head">
          <span>{{ t('检查项') }}</span>
          <span>{{ t('状态') }}</span>
          <span>{{ t('检查详情') }}</span>
        </div>
        <div v-for="row in group.rows" :key="row.title" class="row">
          <strong>{{ row.title }}</strong>
          <nexus-badge :tone="row.ready ? 'success' : 'warning'" :label="row.status" />
          <span>{{ row.detail }}</span>
        </div>
      </section>
    </template>
    <div class="actions">
      <button :disabled="busy" @click="read">{{ t('刷新诊断') }}</button>
      <button :disabled="exporting" @click="support">{{ t('导出脱敏诊断包') }}</button>
    </div>
  </div>
</template>

<style scoped>
.diagnostics {
  display: grid;
  gap: 16px;
  min-width: 0;
}
.overview {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding-bottom: 16px;
  border-bottom: 1px solid var(--border);
}
.overview strong {
  font-size: 18px;
}
p {
  margin: 4px 0 0;
  color: var(--muted);
}
[role='alert'] {
  color: var(--nx-color-danger);
}
.group {
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: 10px;
}
header {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 52px;
  padding: 0 12px;
  background: var(--content-card-soft);
}
header nexus-badge {
  margin-left: auto;
}
.number {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: 50%;
  color: var(--accent);
  background: var(--accent-soft);
}
.row {
  display: grid;
  grid-template-columns: 40% 20% minmax(0, 1fr);
  align-items: center;
  gap: 12px;
  min-height: 48px;
  padding: 0 12px;
  border-top: 1px solid var(--border);
}
.row > :last-child {
  text-align: right;
  overflow-wrap: anywhere;
}
.row nexus-badge {
  justify-self: start;
}
.table-head {
  min-height: 42px;
  color: var(--muted);
  font-size: 12px;
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
@media (max-width: 600px) {
  .row {
    grid-template-columns: 1fr auto;
  }
  .row > :last-child {
    grid-column: 1 / -1;
    text-align: left;
    padding-bottom: 12px;
  }
}
</style>
