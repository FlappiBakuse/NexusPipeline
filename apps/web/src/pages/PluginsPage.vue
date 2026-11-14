<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { confirmAction } from '../app/confirmation';
import { locale, t } from '../app/locale';
import { usePluginBrowser, compatiblePlugin, type PluginRow } from './plugin-browser';
import { PluginOwner } from './plugin-owner';
import { usePluginHistory } from './plugin-history';
import PluginDetails from '../components/PluginDetails.vue';
import PluginFilters from '../components/PluginFilters.vue';
import './plugin-messages';
import { usePanelHeight } from '../app/panel-height';

const props = defineProps<{ store: AppStore; visible: boolean }>();
const browser = usePluginBrowser(props.store, () => props.visible),
  owner = new PluginOwner(props.store);
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
const locked = computed(() => busy.value || session.busy || !!session.pending);
const canManage = computed(() => props.store.can('extensions.write'));
const descriptionTarget = ref<HTMLElement | null>(null);
const workspace = ref<HTMLElement>(),
  mobileDetail = ref(false);
const { height: workspaceHeight, measure } = usePanelHeight(workspace, () => props.visible);
const { history, historyNext, historyBusy, loadHistory } = usePluginHistory(
  props.store,
  () => props.visible,
  () => session.pending
);
onMounted(() => {
  descriptionTarget.value = document.getElementById('page-description-override');
});
const kindLabel = (row: PluginRow): string => t(row.kind === 'data-specialized' ? '专项插件' : '通用插件');
const compatible = (row: PluginRow) =>
  compatiblePlugin(row, props.store.state.capabilities?.hostVersion || '0.0.0');
function status(row: PluginRow): string {
  if (session.pending?.extensionId === row.extensionId) return t('待应用');
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
  await owner.enable(row.installed);
  if (props.visible) await browser.load();
}
async function inspect(retry = false): Promise<void> {
  if (retry) await owner.retry();
  else await owner.inspect();
  if (props.visible && !session.pending) await browser.load();
}
const updates = computed(() => browser.rows.value.filter((row) => row.update && compatible(row)));
async function updateAll(): Promise<void> {
  if (locked.value || !canManage.value) return;
  for (const row of updates.value) {
    await apply(row, 'upgrade');
    if (session.pending || session.error || !props.visible) break;
  }
}
const selectedHistory = computed(() => history.value.filter((item) => item.extensionId === selectedId.value));
watch(view, () => {
  mobileDetail.value = false;
});
watch([() => session.pending, () => session.error, error, () => props.visible], measure, { flush: 'post' });
function select(id: string): void {
  mobileDetail.value = true;
  void browser.select(id);
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
      <button v-if="session.pending" :disabled="session.busy || !store.can('operations.read')" @click="inspect()">{{ t('读取操作结果') }}</button>
    <button v-if="session.pending && session.retryable" :disabled="session.busy || !canManage" @click="inspect(true)">{{ t('重试应用') }}</button>
  </PageActions>
  <Teleport :to="descriptionTarget || 'body'" :disabled="!descriptionTarget">
    <span v-if="visible && view === 'store'">{{ t('浏览官方插件并管理安装版本。') }}</span>
  </Teleport>
  <nexus-toast v-if="session.pending" tone="warning" :message.prop="t('原操作尚待确认，请先读取结果。')" />
  <nexus-toast v-if="session.error || error" tone="danger" :message.prop="t(session.error || error)" />
  <nexus-toast v-else-if="session.notice" :message.prop="t(session.notice)" @nexus-close="session.notice = ''" />
  <div
    ref="workspace"
    class="plugin-workspace"
    :class="{ store: view === 'store', detail: mobileDetail }"
    :style="{ height: workspaceHeight }"
  >
    <div class="plugin-list-column">
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
        :aria-busy="busy"
      >
        <div class="plugin-list-items">
          <nexus-loading-state
            v-if="busy && !browser.rows.value.length"
            :progress.prop="null"
            :label="t('正在加载插件…')"
          />
          <nexus-empty-state
            v-else-if="!filtered.length"
            :title="t('没有匹配的插件')"
            :description="t('调整搜索或筛选条件后重试。')"
          />
          <button
            v-for="row in filtered"
            :key="row.extensionId"
            class="plugin-row"
            :class="{ selected: selectedId === row.extensionId }"
            :aria-pressed="selectedId === row.extensionId"
            @click="select(row.extensionId)"
          >
            <strong>{{ row.name }}</strong>
            <span class="row-summary">{{ row.summary || row.extensionId }}</span>
            <span class="badges">
              <nexus-badge
                :tone="row.kind === 'data-specialized' ? 'info' : 'neutral'"
                :label="kindLabel(row)"
              />
              <nexus-badge :tone="tone(row)" :label="status(row)" />
              <nexus-badge v-if="row.version" tone="neutral" :label="'v' + row.version" />
            </span>
          </button>
        </div>
      </nexus-scroll-area>
    </div>
    <div class="plugin-detail-column">
      <button class="plugin-back" @click="mobileDetail = false">{{ t('返回插件列表') }}</button>
      <PluginDetails
        v-if="selected"
        :key="view + '/' + selected.extensionId"
        :row="selected"
        :details="details"
        :loading="detailLoading"
        :error="detailError"
        :status="status(selected)"
        :tone="tone(selected)"
        :kind-label="kindLabel(selected)"
      >
        <template #actions>
          <button
            v-if="view === 'installed' && selected.installed?.unavailableReason === 'extension_files_changed'"
            :disabled="locked || !canManage"
            @click="apply(selected, 'remove')"
          >
            {{ t('卸载插件') }}
          </button>
          <button
            v-if="view === 'installed'"
            :disabled="locked || !canManage || !store.can('settings.write')"
            @click="enable(selected)"
          >
            {{ t(selected.installed?.desiredEnabled ? '禁用插件' : '启用插件') }}
          </button>
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
          <p v-if="historyBusy && !selectedHistory.length">{{ t('正在读取更新记录…') }}</p>
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
      <footer v-if="view === 'store'" class="plugin-store-footer">
        <small>{{ t('目录更新时间') }}：{{ catalog?.publishedAt || t('未提供') }}</small>
        <div>
          <button class="primary" :disabled="locked || !canManage || !updates.length" @click="updateAll">
            {{ t('更新所有插件') }}
          </button>
          <button :disabled="locked || !canManage" @click="browser.load(true)">{{ t('刷新仓库') }}</button>
        </div>
      </footer>
    </div>
  </div>
</template>
<style scoped src="./plugins-page.css"></style>
