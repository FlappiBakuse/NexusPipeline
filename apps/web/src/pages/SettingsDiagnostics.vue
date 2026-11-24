<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { enumValue, integerValue, objectValue, stringValue } from '../control/client';
import { BlobDownloads } from '../control/blob-downloads';
import { registerMessages, t } from '../app/locale';
import { diagnosticCopy, diagnosticPresentation, diagnosticStatuses, diagnosticTones, type DiagnosticCheck } from './diagnostic-copy';
import { historyTime } from './history-format';

const props = defineProps<{ store: AppStore; visible: boolean }>();
interface Report {
  admission: string;
  extensions: number;
  capturedAt: string;
  framework: string;
  architecture: string;
  checks: DiagnosticCheck[];
}
const report = ref<Report>();
const busy = ref(false);
const exporting = ref(false);
const readError = ref(false);
const exportError = ref(false);
const expanded = ref<string[]>([]);
let owner: AbortController | undefined;
let exportOwner: AbortController | undefined;
const downloads = new BlobDownloads();
const categories = [
  { id: 'environment', title: '运行环境', prefixes: ['host.', 'web.', 'mcp.', 'dependency.'] },
  { id: 'recovery', title: '数据与恢复', prefixes: ['recovery.', 'update.', 'config.', 'execution.', 'scheduler.', 'logs.'] },
  { id: 'plugins', title: '插件状态', prefixes: ['plugin.'] }
];
function categoryFor(id: string) {
  const category = categories.find(group => group.prefixes.some(prefix => id.startsWith(prefix)));
  if (!category) throw new Error('diagnostic_check_invalid');
  return category;
}
function countsFor(items: readonly DiagnosticCheck[]) {
  const counts = { fail: 0, warn: 0, notRun: 0, pass: 0, notRequired: 0 };
  for (const item of items) counts[item.status]++;
  return counts;
}
const rows = computed(() => (report.value?.checks ?? []).map(item => {
  const category = categoryFor(item.id);
  const { count: countLabel, ...presentation } = diagnosticPresentation(item, report.value!.admission);
  return { ...item, ...presentation, category: category.id, categoryTitle: category.title, countLabel };
}));
const counts = computed(() => countsFor(report.value?.checks ?? []));
const findings = computed(() => diagnosticStatuses.slice(0, 3).flatMap(status => rows.value.filter(row => row.status === status)));
const groups = computed(() => categories.map(category => {
  const checks = rows.value.filter(row => row.category === category.id);
  const summary = countsFor(checks);
  return { ...category, checks, summary };
}).filter(group => group.checks.length > 0));
const allExpanded = computed(() => groups.value.length > 0 && groups.value.every(group => expanded.value.includes(group.id)));
const canRead = computed(() => props.store.can('control.doctor.read'));
const overall = computed(() => counts.value.fail ? 'fail' : counts.value.warn ? 'warn' : counts.value.notRun ? 'notRun' : 'pass');
const overallNames = { fail: '存在失败', warn: '需关注', notRun: '有检查未验证', pass: '正常' };
const admissionNames: Record<string, string> = { ready: '就绪', blocked: '受限', changedDuringRead: '读取期间状态变化' };

registerMessages({
  存在失败: 'Issues detected', 有检查未验证: 'Checks unverified', 无需处理: 'No action needed',
  检查时间: 'Checked at', 宿主状态: 'Host status', 已安装插件: 'Installed extensions',
  就绪: 'Ready', 受限: 'Restricted', 读取期间状态变化: 'Changed during the check',
  检查状态计数: 'Check counts by status', 优先处理与关注: 'Issues to review',
  未验证项目: 'Unverified checks', 全部检查明细: 'All check details',
  展开全部: 'Expand all', 收起全部: 'Collapse all', 下一步: 'Next step',
  '{count} 项检查': '{count} checks'
});
const checkCount = (count: number) => t('{count} 项检查').replace('{count}', String(count));

function toggleGroup(id: string, event: Event) {
  const open = (event.currentTarget as HTMLDetailsElement).open;
  if (open === expanded.value.includes(id)) return;
  expanded.value = open ? [...expanded.value, id] : expanded.value.filter(value => value !== id);
}
function toggleAll() {
  expanded.value = allExpanded.value ? [] : groups.value.map(group => group.id);
}
function stopReading() {
  owner?.abort();
  owner = undefined;
  busy.value = false;
}
function stopExporting(): void {
  exportOwner?.abort();
  exportOwner = undefined;
  exporting.value = false;
  downloads.release();
}
watch(() => props.visible, visible => {
  if (!visible) { stopReading(); stopExporting(); }
  else if (!report.value) void read();
}, { immediate: true });
watch(canRead, allowed => { if (!allowed) stopExporting(); });
onBeforeUnmount(() => { stopReading(); stopExporting(); });

async function read(): Promise<void> {
  if (!props.visible || busy.value || !canRead.value) return;
  const current = new AbortController();
  owner = current;
  busy.value = true;
  readError.value = false;
  try {
    const value = await props.store.client.read('/control/v1/doctor', objectValue, current.signal);
    const details = objectValue(value.details);
    const next = {
      admission: stringValue(value.admission),
      extensions: integerValue(details.installedExtensionCount),
      capturedAt: stringValue(value.capturedAtUtc),
      framework: stringValue(details.frameworkVersion),
      architecture: stringValue(details.processArchitecture),
      checks: readChecks(details.checks)
    };
    current.signal.throwIfAborted();
    report.value = next;
  } catch {
    if (!current.signal.aborted) readError.value = true;
  } finally {
    if (owner === current) {
      owner = undefined;
      busy.value = false;
    }
  }
}
function readChecks(value: unknown): DiagnosticCheck[] {
  if (!Array.isArray(value) || value.length < 1 || value.length > 32) throw new Error('diagnostic_checks_invalid');
  return value.map(item => {
    const check = objectValue(item);
    const id = stringValue(check.id), status = enumValue(check.status, diagnosticStatuses), code = stringValue(check.code);
    categoryFor(id);
    diagnosticCopy(id); diagnosticCopy(code);
    return { id, status, code, count: check.count === null ? null : integerValue(check.count) };
  });
}
async function support(): Promise<void> {
  if (!props.visible || exporting.value || !canRead.value) return;
  const current = new AbortController();
  exportOwner = current;
  const signal = AbortSignal.any([current.signal, props.store.client.authoritySignal]);
  exporting.value = true;
  exportError.value = false;
  try {
    const response = await props.store.client.response('/control/v1/support', signal);
    const blob = await response.blob();
    signal.throwIfAborted();
    downloads.save(blob, 'nexus-support.zip');
  } catch {
    if (!signal.aborted) exportError.value = true;
  } finally {
    if (exportOwner === current) {
      exportOwner = undefined;
      exporting.value = false;
    }
  }
}
</script>

<template>
  <div class="diagnostics" :aria-busy="busy">
    <nexus-toast v-if="visible && exportError" tone="danger" :message.prop="t('诊断导出失败，请重试。')" />
    <section class="diagnostic-overview" data-surface="raised" :aria-label="t('总体状态')">
      <div class="diagnostic-heading">
        <div class="diagnostic-title">
          <h3>{{ t('总体状态') }}</h3>
          <nexus-badge v-if="report" :tone="diagnosticTones[overall]" :label="t(overallNames[overall])" />
        </div>
        <div class="diagnostic-actions">
          <button type="button" :disabled="busy || !canRead" @click="read">{{ t('刷新诊断') }}</button>
          <button type="button" :disabled="exporting || !canRead" @click="support">{{ t('导出脱敏诊断包') }}</button>
        </div>
      </div>
      <template v-if="report">
        <p class="diagnostic-context">
          <span>{{ checkCount(report.checks.length) }}</span>
          <span>{{ t('检查时间') }} {{ historyTime(report.capturedAt) }}</span>
          <span>{{ t('宿主状态') }} {{ t(admissionNames[report.admission] ?? report.admission) }}</span>
          <span>{{ t('已安装插件') }} {{ report.extensions }}</span>
        </p>
        <dl class="diagnostic-totals" :aria-label="t('检查状态计数')">
          <div v-for="status in diagnosticStatuses" :key="status" :data-status="status" :data-active="counts[status] > 0">
            <dt>{{ diagnosticCopy(status) }}</dt>
            <dd>{{ counts[status] }}</dd>
          </div>
        </dl>
      </template>
      <nexus-spinner v-else-if="busy" :label="t('诊断加载中…')" />
      <div v-if="readError" class="diagnostic-read-error">
        <p role="alert">{{ t('诊断读取失败，请重试。') }}</p>
        <button type="button" :disabled="busy" @click="read">{{ t('重试') }}</button>
      </div>
    </section>

    <template v-if="report">
      <template v-for="section in [
        { label: '优先处理与关注', items: findings.filter(item => item.status !== 'notRun') },
        { label: '未验证项目', items: findings.filter(item => item.status === 'notRun') }
      ]" :key="section.label">
        <section v-if="section.items.length" class="diagnostic-priority" :aria-label="t(section.label)">
          <h4>{{ t(section.label) }}</h4>
          <article v-for="item in section.items" :key="item.id" class="diagnostic-finding" :data-status="item.status" data-surface="raised">
            <header>
              <nexus-badge :tone="diagnosticTones[item.status]" :label="diagnosticCopy(item.status)" />
              <h5>{{ item.title }}</h5>
              <span class="diagnostic-finding-category">{{ t(item.categoryTitle) }}</span>
            </header>
            <p>{{ item.observation }}<span v-if="item.countLabel" class="diagnostic-value"> · {{ item.countLabel }}</span></p>
            <p v-if="item.action" class="diagnostic-next"><strong>{{ t('下一步') }}</strong> {{ item.action }}</p>
          </article>
        </section>
      </template>
      <p v-if="!findings.length" class="diagnostic-clear">{{ t('无需处理') }}</p>
      <section class="diagnostic-details" :aria-label="t('全部检查明细')">
        <div class="diagnostic-heading">
          <h4>{{ t('全部检查明细') }}</h4>
          <button type="button" @click="toggleAll">{{ t(allExpanded ? '收起全部' : '展开全部') }}</button>
        </div>
        <details v-for="(group, index) in groups" :key="group.id" class="diagnostic-group animated-disclosure"
          :open="expanded.includes(group.id)" @toggle="toggleGroup(group.id, $event)">
          <summary class="diagnostic-category">
            <span class="diagnostic-category-content">
              <span class="diagnostic-category-number" aria-hidden="true">{{ index + 1 }}</span>
              <strong>{{ t(group.title) }}</strong>
              <span class="diagnostic-value">{{ checkCount(group.checks.length) }}</span>
            </span>
            <span class="diagnostic-category-status">
              <template v-for="status in diagnosticStatuses" :key="status">
                <nexus-badge v-if="group.summary[status]" :tone="diagnosticTones[status]" :label="group.summary[status] + ' ' + diagnosticCopy(status)" />
              </template>
            </span>
            <nexus-icon class="disclosure-chevron" name="chevronRight" aria-hidden="true" />
          </summary>
          <ul class="diagnostic-checks" role="list">
            <li v-for="item in group.checks" :key="item.id" class="diagnostic-check" :data-status="item.status">
              <strong class="diagnostic-check-title">{{ item.title }}</strong>
              <nexus-badge :tone="diagnosticTones[item.status]" :label="diagnosticCopy(item.status)" />
              <div class="diagnostic-check-body">
                <p class="diagnostic-observation">
                  <span>{{ item.observation }}</span>
                  <span v-if="item.countLabel" class="diagnostic-unit">{{ item.countLabel }}</span>
                </p>
                <p v-if="item.id === 'host.version'" class="diagnostic-check-scope">{{ report.framework }} · {{ report.architecture }}</p>
                <p v-if="item.scope" class="diagnostic-check-scope">{{ item.scope }}</p>
                <p v-if="item.action" class="diagnostic-check-next"><strong>{{ t('下一步') }}</strong> {{ item.action }}</p>
              </div>
            </li>
          </ul>
        </details>
      </section>
    </template>
  </div>
</template>

<style scoped>
.diagnostics, .diagnostic-priority, .diagnostic-details {
  display: grid;
  gap: 12px;
  min-inline-size: 0;
}
.diagnostics { gap: 16px; }
.diagnostic-heading, .diagnostic-title, .diagnostic-actions, .diagnostic-finding header {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}
.diagnostic-heading { justify-content: space-between; }
.diagnostic-overview, .diagnostic-finding, .diagnostic-details details {
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
}
.diagnostic-overview {
  display: grid;
  gap: 10px;
  padding: 14px;
}
.diagnostics :is(h3, h4, h5, p, dl, dd) { margin: 0; overflow-wrap: anywhere; }
.diagnostic-title h3 { font-size: 17px; }
.diagnostics :is(h4, h5) { font-size: 14px; }
.diagnostic-context {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  color: var(--muted);
  font-size: 12px;
}
.diagnostic-totals {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 103px), 1fr));
  gap: 8px;
}
.diagnostic-totals > div {
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  background: var(--content-control);
  color: var(--muted);
}
.diagnostic-totals dt { font-size: 12px; }
.diagnostic-totals dd { font-size: 17px; font-weight: 700; font-variant-numeric: tabular-nums; }
.diagnostic-totals [data-status='fail'][data-active='true'] { color: var(--bad); background: var(--bad-soft); }
.diagnostic-totals [data-status='warn'][data-active='true'] { color: var(--warn); background: var(--warn-soft); }
.diagnostic-totals [data-status='notRun'][data-active='true'] { color: var(--accent); background: var(--accent-soft); }
.diagnostic-totals [data-status='pass'][data-active='true'] { color: var(--ok); }
.diagnostic-finding {
  display: grid;
  gap: 7px;
  padding: 12px;
  border-inline-start: 3px solid var(--accent);
}
.diagnostic-finding[data-status='fail'] { border-inline-start-color: var(--bad); }
.diagnostic-finding[data-status='warn'] { border-inline-start-color: var(--warn); }
.diagnostic-finding-category, .diagnostic-value, .diagnostic-clear { color: var(--muted); }
.diagnostic-finding-category, .diagnostic-value { font-size: 12px; }
.diagnostic-next {
  padding: 8px 10px;
  border-radius: var(--radius-sm);
  background: var(--content-control);
}
.diagnostic-next strong { margin-inline-end: 4px; }
.diagnostic-read-error { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; color: var(--bad); }
.diagnostic-details { container: diagnostic-categories / inline-size; }
.diagnostic-group { background: var(--content-card-soft); min-inline-size: 0; }
.diagnostic-category {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto max-content;
  align-items: center;
  gap: 6px 10px;
  min-block-size: 44px;
  padding: 10px 14px;
}
.diagnostic-category-content, .diagnostic-category-status, .diagnostic-observation {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 5px 8px;
}
.diagnostic-category-content { min-inline-size: 0; }
.diagnostic-category-content strong { color: var(--text); }
.diagnostic-category-number {
  display: grid;
  place-items: center;
  inline-size: 24px;
  block-size: 24px;
  border-radius: 50%;
  background: var(--accent-soft);
  color: var(--accent);
  font-size: 12px;
  font-weight: 700;
}
.diagnostic-checks {
  list-style: none;
  padding: 0;
  margin: 0;
  border-block-start: 1px solid var(--border);
  border-end-start-radius: var(--radius-md);
  border-end-end-radius: var(--radius-md);
  background: var(--content-card);
}
.diagnostic-check {
  display: grid;
  grid-template-columns: minmax(130px, 195px) max-content minmax(0, 1fr);
  align-items: start;
  gap: 6px 12px;
  padding: 10px 14px;
  min-inline-size: 0;
}
.diagnostic-check + .diagnostic-check { border-block-start: 1px solid var(--border); }
.diagnostic-check-title { font-size: 13px; overflow-wrap: anywhere; }
.diagnostic-check > nexus-badge { justify-self: start; }
.diagnostic-check-body {
  display: grid;
  gap: 3px;
  min-inline-size: 0;
  font-size: 13px;
}
.diagnostic-check[data-status='notRequired'] .diagnostic-observation,
.diagnostic-check-scope { color: var(--muted); }
.diagnostic-check-scope, .diagnostic-check-next { font-size: 12px; line-height: 1.5; }
.diagnostic-check-next strong { margin-inline-end: 4px; }
.diagnostic-unit {
  display: inline-block;
  padding: 2px 5px;
  border: 1px solid var(--border);
  border-radius: 5px;
  background: var(--muted-soft);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}
@container diagnostic-categories (inline-size < 35rem) {
  .diagnostic-category, .diagnostic-check { grid-template-columns: minmax(0, 1fr) max-content; }
  .diagnostic-category-status { grid-row: 2; grid-column: 1 / -1; padding-inline-start: 32px; }
  .diagnostic-category .disclosure-chevron { grid-column: 2; grid-row: 1; }
  .diagnostic-check-body { grid-column: 1 / -1; }
}
</style>
