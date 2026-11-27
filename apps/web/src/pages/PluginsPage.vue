<script setup lang="ts">
import { computed, inject, nextTick, onMounted, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { confirmAction } from '../app/confirmation';
import { locale, t } from '../app/locale';
import { usePluginBrowser, compatiblePlugin, pluginRowId, type PluginRow } from './plugin-catalog';
import { localPluginsKey } from './local-plugins';
import { PluginOperations } from './plugin-operations';
import { usePluginHistory } from './plugin-history';
import PluginDetails from '../components/PluginDetails.vue';
import PluginFilters from '../components/PluginFilters.vue';
import './plugin-messages';

const props = defineProps<{ store: AppStore; visible: boolean }>();
const local = inject(localPluginsKey)!;
const scanPending = ref(true);
const installing = computed(() => props.store.state.events?.initialExtensions === 'installing');
const startupReady = computed(() => props.store.state.events !== null && !installing.value);
const readsVisible = () => props.visible && startupReady.value && !scanPending.value;
const localRevision = () => local.revision.value;
const browser = usePluginBrowser(props.store, readsVisible, local.items, localRevision),
  owner = new PluginOperations(props.store);
const {
  view,
  selected,
  selectedId,
  filtered,
  search,
  kind,
  order,
  descending,
  busy,
  error,
  details,
  detailLoading,
  detailError,
  catalog
} = browser;
const session = owner.state;
const confirming = ref(false);
const locked = computed(() => !startupReady.value || scanPending.value || busy.value || session.busy || !!session.pending);
const canManage = computed(() => props.store.can('extensions.write'));
const canScan = computed(() => local.available.value && canManage.value);
const hasFooterActions = computed(() => view.value === 'store' || canScan.value);
const canEnable = computed(() => canScan.value && props.store.can('settings.write'));
const listColumn = ref<HTMLElement | null>(null);
const detailColumn = ref<HTMLElement | null>(null);
const descriptionTarget = ref<HTMLElement | null>(null);
const mobileDetail = ref(false);
const { history, historyNext, historyBusy, loadHistory } = usePluginHistory(
  props.store,
  readsVisible,
  () => session.pending,
  localRevision
);
onMounted(() => {
  descriptionTarget.value = document.getElementById('page-description-override');
});
const kindLabel = (row: PluginRow): string => row.kind ? t(row.kind === 'data-specialized' ? '专项插件' : '通用插件') : '';
const compatible = (row: PluginRow) =>
  compatiblePlugin(row, props.store.state.capabilities?.hostVersion || '0.0.0');
const unofficial = (row: PluginRow) => !!row.installed?.isUnofficial || row.local?.state === 'pendingApproval';
function status(row: PluginRow): string {
  if (session.pending?.extensionId === row.extensionId) return t('待应用');
  if (row.local) {
    if (row.local.state === 'pendingApproval') return t('待启用');
    if (row.local.state === 'pendingUpdate') return t('待确认更新');
    if (row.local.state === 'awaitingVerification') return t('等待官方校验');
    if (row.local.reason === 'extension_host_version_unsupported') return t('需要更新宿主');
    return t(row.local.reason === 'extension_local_source_unverified' ? '来源待核验' : '不可用');
  }
  if (view.value === 'installed') {
    if (row.installed?.unavailableReason === 'extension_files_changed') return t('插件文件损坏');
    if (row.installed?.unavailableReason === 'extension_directory_unavailable') return t('插件目录不可用');
    if (row.installed?.unavailableReason === 'extension_files_removed') return t('等待移除');
  }
  if (view.value === 'store')
    return t(!compatible(row) ? '需要更新宿主' : row.update ? '可更新' : row.installed ? '已安装' : '未安装');
  return t(
    row.installed?.activationId
      ? '运行中'
      : row.installed?.unavailableReason || row.installed?.desiredEnabled
        ? '待应用'
        : '已禁用'
  );
}
const tone = (row: PluginRow): string =>
  status(row) === t('运行中') || status(row) === t('已安装')
    ? 'success'
    : status(row) === t('已禁用') || status(row) === t('未安装')
      ? 'neutral'
      : 'warning';
async function apply(row: PluginRow, action: 'install' | 'upgrade' | 'remove'): Promise<void> {
  if (locked.value || !canManage.value || (action !== 'remove' && (!row.candidate || !compatible(row))))
    return;
  if (action === 'remove' && !(await confirmAction(t('卸载此插件？运行中的资源将由宿主协调。')))) return;
  await owner.apply(
    action === 'remove' ? row.installed! : row.candidate!,
    action,
    row.installation,
    catalog.value
  );
  if (props.visible && !session.pending) await browser.load();
}
async function enable(row: PluginRow): Promise<void> {
  if (locked.value || !row.installed) return;
  const directory = await owner.enable(row.installed);
  if (directory) await browser.adoptDirectory(directory);
  else if (props.visible) await browser.load();
}
async function enableLocal(row: PluginRow): Promise<void> {
  const item = row.local;
  if (!item || (item.state !== 'pendingApproval' && item.state !== 'pendingUpdate') ||
    locked.value || confirming.value || !canEnable.value) return;
  const update = item.state === 'pendingUpdate';
  const authority = props.store.client.authoritySignal;
  const risk = t(item.kind === 'managed-code'
    ? '发布者未经官方验证。插件代码会在宿主进程中运行，并使用宿主进程的操作系统权限。请确认信任此文件后继续。'
    : '发布者未经官方验证。请核对专项插件申报的能力和适用范围，并确认信任此文件后继续。');
  const context = item.kind === 'data-specialized'
    ? '\n\n' + t('适用项目') + ' · ' + (item.gameName || t('未提供')) + '\n' +
      t('申报能力') + ' · ' + (item.capabilities.join(' · ') || t('未申报'))
    : '';
  confirming.value = true;
  try {
    const message = update ? t('将使用此本地发行包更新插件，并保留当前启用设置。') : risk + context;
    if (!(await confirmAction(`${row.name} · v${row.version}\n\n${message}\n\n${item.relativePath}`,
      authority, { title: t(update ? '更新本地官方插件？' : '启用非官方插件？'),
        confirmLabel: t(update ? '更新插件' : '确认启用') })) ||
      !props.visible || authority.aborted || !canEnable.value) return;
    const candidate = local.items.value.find(value => value.candidateId === item.candidateId);
    if (!candidate || candidate.state !== item.state) {
      session.errorExtensionId = item.extensionId;
      session.error = 'extension_local_candidate_changed';
    } else await owner.enableLocal(candidate);
  } finally {
    confirming.value = false;
  }
  if (session.error === 'extension_local_candidate_changed' || (!session.pending && !session.error)) {
    await scanLocal();
    if (!local.error.value) return;
  }
  await local.refresh();
  if (props.visible && !session.pending) await browser.load();
}
async function inspect(retry = false): Promise<void> {
  const localOperation = session.pending?.kind === 'local';
  const directory = await (retry ? owner.retry() : owner.inspect());
  if (localOperation) {
    if (!session.pending) {
      await scanLocal();
      if (!local.error.value) return;
    }
    await local.refresh();
  }
  if (directory) await browser.adoptDirectory(directory);
  else if (props.visible && !session.pending) await browser.load();
}
const updates = computed(() => browser.rows.value.filter((row) => row.update && compatible(row)));
async function updateAll(): Promise<void> {
  if (locked.value || !canManage.value) return;
  for (const row of updates.value) {
    await apply(row, 'upgrade');
    if (session.pending || session.error || !props.visible) break;
  }
}
const selectedHistory = computed(() => history.value.filter((item) => item.extensionId === selected.value?.extensionId));
let localWork: Promise<void> | undefined;
function scanLocal(onEntry = false): Promise<void> {
  if (localWork) return localWork;
  if (!startupReady.value) return Promise.resolve();
  scanPending.value = true;
  localWork = (async () => {
    // Let the page readers release their prepared reads before scan invalidates them.
    await nextTick();
    if (onEntry && !local.available.value) await local.refresh();
    if (startupReady.value && (!onEntry || props.visible) && canScan.value) await local.scan();
  })().finally(() => {
    localWork = undefined;
    scanPending.value = false;
  });
  return localWork;
}
watch([() => props.visible, startupReady], ([visible, ready]) => {
  if (visible && ready) void scanLocal(true);
}, { immediate: true, flush: 'sync' });
watch(view, () => {
  mobileDetail.value = false;
});
async function select(id: string): Promise<void> {
  mobileDetail.value = true;
  void browser.select(id);
  if (matchMedia('(max-width: 719px)').matches) {
    await nextTick();
    detailColumn.value?.querySelector<HTMLElement>('h3')?.focus();
  }
}
async function backToList(): Promise<void> {
  mobileDetail.value = false;
  await nextTick();
  listColumn.value?.querySelector<HTMLElement>('[aria-pressed="true"]')?.focus();
}
</script>
<template>
  <PageActions :visible="visible">
    <button
      class="plugin-tab"
      :class="{ primary: view === 'installed' }"
      :aria-pressed="view === 'installed'"
      @click="view = 'installed'"
    >
      {{ t('本地插件') }}
    </button>
    <button
      class="plugin-tab"
      :class="{ primary: view === 'store' }"
      :aria-pressed="view === 'store'"
      @click="view = 'store'"
    >
      {{ t('插件仓库') }}
    </button>
    <button v-if="session.pending" type="button" :disabled="session.busy || !store.can('operations.read')" @click="inspect()">{{ t('读取操作结果') }}</button>
    <button v-if="session.pending && session.retryable" :disabled="session.busy || !canManage" @click="inspect(true)">{{ t('重试应用') }}</button>
  </PageActions>
  <Teleport :to="descriptionTarget || 'body'" :disabled="!descriptionTarget">
    <span v-if="visible && view === 'store'">{{ t('浏览官方插件并管理安装版本。') }}</span>
  </Teleport>
  <template v-if="visible">
    <nexus-toast v-if="session.error && !session.errorExtensionId" :tone="session.pending ? 'warning' : 'danger'" :message.prop="t(session.error)" />
    <nexus-toast v-else-if="session.notice" :message.prop="t(session.notice)" @nexus-close="session.notice = ''" />
    <nexus-toast v-else-if="session.pending && !session.busy" tone="warning" :message.prop="t('原操作尚待确认，请先读取结果。')" />
    <nexus-toast v-else-if="!session.pending && error" tone="danger" :message.prop="t(error)" />
    <nexus-toast v-if="local.error.value" tone="danger" :message.prop="t(local.error.value)" @nexus-close="local.error.value = ''" />
  </template>
  <div class="plugin-workspace" :class="{ store: view === 'store', detail: mobileDetail, 'footer-actions': hasFooterActions }">
    <div ref="listColumn" class="plugin-list-column">
      <div class="plugin-search">
        <nexus-text-input
          :value.prop="search"
          :placeholder="t('搜索插件名称、标签或游戏')"
          :aria-label="t('搜索插件名称、标签或游戏')"
          @nexus-change="search = $event.detail.value"
        />
        <PluginFilters
          :kind="kind"
          :order="order"
          :descending="descending"
          :visible="visible"
          @kind="kind = $event"
          @order="order = $event"
          @descending="descending = $event"
        />
      </div>
      <nexus-scroll-area
        class="plugin-list"
        :label="t(view === 'installed' ? '本地插件' : '插件仓库')"
        :aria-label="t(view === 'installed' ? '本地插件' : '插件仓库')"
        :aria-busy="!startupReady || scanPending || busy || (view === 'installed' && local.busy.value)"
      >
        <div class="plugin-list-items">
          <nexus-loading-state
            v-if="(!startupReady || scanPending || busy) && !browser.rows.value.length"
            :progress.prop="null"
            :label="t(installing ? '正在安装随附插件…' : '正在加载插件…')"
          />
          <nexus-empty-state
            v-else-if="!filtered.length"
            :title="t('没有匹配的插件')"
            :description="t('调整搜索或筛选条件后重试。')"
          />
          <button
            v-for="row in filtered"
            :key="pluginRowId(row)"
            type="button"
            class="plugin-row"
            :class="{ selected: selectedId === pluginRowId(row) }"
            :aria-pressed="selectedId === pluginRowId(row)"
            @click="select(pluginRowId(row))"
          >
            <strong>{{ row.name }}</strong>
            <span class="row-summary">{{ row.summary || row.extensionId }}</span>
            <span class="badges">
              <nexus-badge
                v-if="row.kind"
                :tone="row.kind === 'data-specialized' ? 'info' : 'neutral'"
                :label="kindLabel(row)"
              />
              <nexus-badge :tone="tone(row)" :label="status(row)" />
              <nexus-badge v-if="row.version" tone="neutral" :label="'v' + row.version" />
              <nexus-badge v-if="unofficial(row)" tone="warning" :label="t('非官方')" />
            </span>
          </button>
        </div>
      </nexus-scroll-area>
    </div>
    <div ref="detailColumn" class="plugin-detail-column">
      <button class="plugin-back" type="button" @click="backToList">{{ t('返回插件列表') }}</button>
      <PluginDetails
        v-if="selected"
        :key="view + '/' + pluginRowId(selected)"
        :row="selected"
        :details="details"
        :loading="detailLoading"
        :error="detailError"
        :status="status(selected)"
        :tone="tone(selected)"
        :kind-label="kindLabel(selected)"
        :unofficial="unofficial(selected)"
        :action-error="session.errorExtensionId === selected.extensionId ? session.error : ''"
      >
        <template #actions>
          <template v-if="selected.local">
            <button v-if="(selected.local.state === 'pendingApproval' || selected.local.state === 'pendingUpdate') && canEnable"
              class="primary" type="button" :disabled="locked || local.busy.value" @click="enableLocal(selected)">
              {{ t(selected.local.state === 'pendingUpdate' ? '更新插件' : '启用插件') }}
            </button>
          </template>
          <template v-else-if="view === 'installed'">
            <button
              v-if="selected.installed?.isUnofficial || selected.installed?.unavailableReason === 'extension_files_changed'"
              :disabled="locked || !canManage"
              @click="apply(selected, 'remove')"
            >
              {{ t('卸载插件') }}
            </button>
            <button
              :disabled="locked || !canManage || !store.can('settings.write')"
              @click="enable(selected)"
            >
              {{ t(selected.installed?.desiredEnabled ? '禁用插件' : '启用插件') }}
            </button>
          </template>
          <template v-else>
            <button
              v-if="selected.installed"
              :disabled="locked || !canManage"
              @click="apply(selected, 'remove')"
            >
              {{ t('卸载插件') }}
            </button>
            <button
              v-if="!selected.installed || selected.update"
              class="primary"
              :disabled="locked || !canManage || !compatible(selected)"
              @click="apply(selected, selected.installed ? 'upgrade' : 'install')"
            >
              {{ t(selected.installed ? '更新插件' : '安装插件') }}
            </button>
          </template>
        </template>
        <template #history>
          <nexus-spinner v-if="historyBusy && !selectedHistory.length" :label="t('正在读取更新记录…')" />
          <ol v-else-if="selectedHistory.length" class="plugin-history">
            <li v-for="item in selectedHistory" :key="String(item.operationId)">
              <time>{{ new Date(String(item.createdAt)).toLocaleString(locale) }}</time>
              <span>{{ t(String(item.action)) }} · {{ t(String(item.outcome)) }}</span>
            </li>
          </ol>
          <p v-else class="empty-history">{{ t('暂无更新记录') }}</p>
          <button v-if="historyNext !== null" :disabled="historyBusy" @click="loadHistory(false)">
            {{ t('读取更多记录') }}
          </button>
        </template>
      </PluginDetails>
      <div v-else class="plugin-placeholder">
        <nexus-empty-state :title="t('选择一个插件')" :description="t('从左侧列表选择插件查看完整信息。')" />
      </div>
      <footer v-if="hasFooterActions" class="plugin-footer">
        <small v-if="view === 'store'">{{ t('目录更新时间') }}：{{ catalog?.publishedAt || t('未提供') }}</small>
        <div>
          <template v-if="view === 'store'">
            <button class="primary" :disabled="locked || !canManage || !updates.length" @click="updateAll">
              {{ t('更新所有插件') }}
            </button>
            <button type="button" :disabled="locked || !canManage" :aria-busy="busy" @click="browser.load(true)">
              {{ t('刷新仓库') }}
            </button>
          </template>
          <button v-else type="button" :disabled="locked || local.busy.value"
            :aria-busy="scanPending || local.busy.value" @click="scanLocal()">
            {{ t('扫描本地目录') }}
          </button>
        </div>
      </footer>
    </div>
  </div>
</template>
<style scoped>
.plugin-workspace {
  display: grid;
  flex: 1;
  grid-template-columns: minmax(280px, 0.5625fr) minmax(0, 1fr);
  grid-template-rows: minmax(0, 1fr);
  gap: 18px;
  align-items: stretch;
  min-height: 0;
}
.plugin-list-column,
.plugin-detail-column {
  min-width: 0;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.plugin-list-column {
  gap: 12px;
}
.plugin-detail-column {
  gap: 12px;
}
.plugin-search {
  display: flex;
  gap: 8px;
  align-items: start;
}
.plugin-search > nexus-text-input {
  flex: 1;
  width: 0;
  min-width: 0;
}
.plugin-list {
  flex: 1;
  min-height: 0;
  padding: 8px;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--nx-radius-lg);
  background: var(--content-card);
  box-shadow: var(--nx-surface-shadow);
}
.plugin-list-items {
  display: flex;
  flex-direction: column;
}
.plugin-row {
  flex: none;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 2px;
  width: 100%;
  min-height: 86px;
  padding: 8px 14px;
  border: 0;
  border-radius: 0;
  background: transparent;
  box-shadow: none;
  text-align: left;
  line-height: 1.5;
}
.plugin-row + .plugin-row { border-block-start: 1px solid var(--border); }
.plugin-row:hover {
  background: var(--content-control-hover);
}
.plugin-row.selected {
  border-radius: var(--nx-radius-compact);
  background: var(--accent-soft);
  box-shadow: inset 3px 0 var(--accent);
}
.plugin-row > strong {
  font-size: 13px;
  font-weight: 700;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.row-summary {
  color: var(--muted);
  font-size: 12px;
  font-weight: 500;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.badges {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  align-items: center;
  margin-top: 2px;
}
.plugin-details {
  flex: 1;
}
.plugin-list-items > nexus-empty-state,
.plugin-placeholder > nexus-empty-state {
  border: 0;
  background: transparent;
  box-shadow: none;
}
.plugin-placeholder {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 28px;
  border: 1px solid var(--border);
  border-radius: var(--nx-radius-lg);
  background: var(--content-card);
  box-shadow: var(--nx-surface-shadow);
}
.plugin-footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
  flex: none;
}
.plugin-footer > small {
  color: var(--muted);
  font-size: 12px;
  min-width: 0;
  overflow-wrap: anywhere;
}
.plugin-footer > div {
  display: flex;
  justify-content: flex-end;
  flex: none;
  gap: 8px;
  margin-inline-start: auto;
}
.plugin-tab {
  min-width: 84px !important;
}
.plugin-back {
  display: none;
  flex: none;
  width: 100%;
  color: var(--muted);
  background: var(--content-control);
}
.plugin-outcome {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  padding: 10px 0;
}
.plugin-error {
  color: var(--bad);
}
.plugin-notice {
  color: var(--ok);
}
.plugin-error,
.plugin-notice {
  margin: 0 0 12px;
  font-size: 12px;
}
.plugin-history {
  margin: 0;
  padding: 0;
  list-style: none;
  font-size: 12px;
}
.plugin-history > li {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 16px;
  padding: 8px 0;
  border-bottom: 1px solid var(--border);
}
.plugin-history time,
.empty-history {
  color: var(--muted);
  font-size: 12px;
}
.empty-history {
  margin: 0;
}
@media (max-width: 719px) {
  .plugin-workspace {
    grid-template-columns: minmax(0, 1fr);
  }
  .plugin-detail-column {
    display: none;
  }
  .plugin-workspace.detail .plugin-list-column {
    display: none;
  }
  .plugin-workspace.detail .plugin-detail-column {
    display: flex;
  }
  .plugin-workspace.footer-actions:not(.detail) {
    grid-template-rows: minmax(0, 1fr) auto;
  }
  .plugin-workspace.footer-actions:not(.detail) .plugin-detail-column {
    display: contents;
  }
  .plugin-workspace.footer-actions:not(.detail) .plugin-detail-column > :not(.plugin-footer) {
    display: none;
  }
  .plugin-back {
    display: block;
  }
  .plugin-footer {
    flex-wrap: wrap;
  }
}
@media (max-width: 600px) {
  .plugin-tab,
  .plugin-back {
    min-height: 44px;
  }
}
</style>
