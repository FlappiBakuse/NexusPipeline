<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from "vue";
import { api, isAbortError } from "../../platform/api";
import { filterAndSortPlugins, defaultPluginViewState, isPluginViewStateActive } from "../../platform/plugin-list";
import { t } from "../../platform/i18n";
import { setTopbarTitle } from "../../platform/shell";
import { toast } from "../../platform/toast";
import { useShellStore } from "../../stores/shell";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";
import NxpPageHeader from "../../ui/composites/NxpPageHeader.vue";
import NxpTabs from "../../ui/composites/NxpTabs.vue";
import FilterPopover from "./components/FilterPopover.vue";
import PluginList from "./components/PluginList.vue";
import PluginDetail from "./components/PluginDetail.vue";
import { type PluginViewPlugin, type PluginViewTab } from "./utils/pluginStatusView";
import { refreshStoreRepository } from "./utils/storeRefresh";

type Tab = PluginViewTab;
type Plugin = PluginViewPlugin & { name: string };
interface DetailState { name: string; loading: boolean; error: string; data: Plugin | null }

const activeTab = ref<Tab>("local");
const filterOpen = ref(false);
const selected = reactive<Record<Tab, string>>({ local: "", store: "" });
const list = reactive<Record<Tab, { loaded: boolean; loading: boolean; error: string; plugins: Plugin[]; available: boolean; stale: boolean; fetchedAt: string }>>({
  local: { loaded: false, loading: false, error: "", plugins: [], available: true, stale: false, fetchedAt: "" },
  store: { loaded: false, loading: false, error: "", plugins: [], available: true, stale: false, fetchedAt: "" },
});
const details = reactive<Record<Tab, DetailState>>({
  local: { name: "", loading: false, error: "", data: null },
  store: { name: "", loading: false, error: "", data: null },
});
const view = reactive<Record<Tab, { query: string; kind: string; sortBy: string; direction: string }>>({
  local: defaultPluginViewState(),
  store: defaultPluginViewState(),
});
const requestSerial = ref(0);
const listSerial = ref(0);
const storeRefreshing = ref(false);
const shell = useShellStore();

const currentList = computed(() => list[activeTab.value]);
const currentView = computed(() => view[activeTab.value]);
const visiblePlugins = computed(() => filterAndSortPlugins(currentList.value.plugins, currentView.value) as Plugin[]);
const detail = computed(() => details[activeTab.value]);
const detailVisibleMobile = ref(false);
const tabs = computed(() => [
  { value: "local", label: t("plugins.local_plugins"), testId: "plugin-local-tab" },
  { value: "store", label: t("plugins.plugin_repository"), testId: "plugin-store-tab" },
]);

function resetDetail(tab: Tab) {
  const name = selected[tab];
  details[tab] = { name, loading: Boolean(name), error: "", data: null };
}
function reconcile(tab: Tab) {
  if (!visiblePlugins.value.length) {
    selected[tab] = "";
    details[tab] = { name: "", loading: false, error: "", data: null };
    return;
  }
  if (!visiblePlugins.value.some(item => item.name === selected[tab])) selected[tab] = visiblePlugins.value[0].name;
  if (details[tab].name !== selected[tab]) resetDetail(tab);
}

async function loadDetail(tab: Tab, force = false) {
  const name = selected[tab];
  if (!name) return;
  const id = ++requestSerial.value;
  const previous = details[tab].data;
  if (!force && previous && details[tab].name === name) return;
  details[tab] = { name, loading: true, error: "", data: previous };
  try {
    const path = tab === "store" ? `/api/plugins/store/${encodeURIComponent(name)}/detail` : `/api/plugins/${encodeURIComponent(name)}/detail`;
    const data = await api("GET", path) as Plugin;
    if (id !== requestSerial.value || selected[tab] !== name || activeTab.value !== tab) return;
    details[tab] = { name, loading: false, error: "", data };
  } catch (reason) {
    if (id !== requestSerial.value || selected[tab] !== name || activeTab.value !== tab || isAbortError(reason)) return;
    details[tab] = { name, loading: false, error: reason instanceof Error ? reason.message : String(reason), data: previous };
  }
}

async function loadList(tab: Tab, force = false) {
  if (list[tab].loading || (list[tab].loaded && !force)) return;
  const id = ++listSerial.value;
  list[tab].loading = true;
  list[tab].error = "";
  try {
    const data = await api<any>("GET", tab === "store" ? "/api/plugins/store" : "/api/plugins");
    if (id !== listSerial.value || activeTab.value !== tab) return;
    const plugins = (tab === "store" ? (data?.plugins || []) : data) as Plugin[];
    list[tab].plugins = Array.isArray(plugins) ? plugins : [];
    list[tab].available = tab !== "store" || data?.available !== false;
    list[tab].stale = tab === "store" && data?.stale === true;
    list[tab].fetchedAt = String(tab === "store" ? data?.fetchedAt || "" : "");
    list[tab].loaded = true;
    list[tab].loading = false;
    reconcile(tab);
    await loadDetail(tab);
  } catch (reason) {
    if (id !== listSerial.value || activeTab.value !== tab || isAbortError(reason)) return;
    list[tab].loading = false;
    list[tab].loaded = true;
    list[tab].available = tab !== "store";
    list[tab].error = reason instanceof Error ? reason.message : String(reason);
    reconcile(tab);
  }
}

function switchTab(tab: string) {
  const next: Tab = tab === "store" ? "store" : "local";
  filterOpen.value = false;
  activeTab.value = next;
  detailVisibleMobile.value = false;
  void loadList(next);
}
function selectPlugin(plugin: PluginViewPlugin) {
  if (!plugin.name) return;
  selected[activeTab.value] = plugin.name;
  detailVisibleMobile.value = true;
  void loadDetail(activeTab.value);
}
function updateFilter(patch: Partial<typeof view.local>) {
  Object.assign(view[activeTab.value], patch);
  reconcile(activeTab.value);
  void loadDetail(activeTab.value);
}
function resetFilter() {
  view[activeTab.value] = defaultPluginViewState();
  reconcile(activeTab.value);
  void loadDetail(activeTab.value);
}
async function runPluginAction(action: string, nameOverride = "") {
  const name = nameOverride || selected[activeTab.value];
  if (!name) return;
  try {
    const result = await api<{ restartRequired?: boolean }>(
      "POST",
      activeTab.value === "store" ? `/api/plugins/store/${encodeURIComponent(name)}/${action}` : `/api/plugins/${encodeURIComponent(name)}/${action}`);
    if (result?.restartRequired === true) shell.markRestartRequired(`plugin.${action}`);
    toast(activeTab.value === "store" && action === "uninstall"
      ? t("plugins.uninstall.queued")
      : t("plugins.status.action_queued"));
    list["local"].loaded = false;
    list["store"].loaded = false;
    await loadList(activeTab.value, true);
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
async function refreshStoreAsync() {
  if (storeRefreshing.value) return;
  storeRefreshing.value = true;
  try {
    await refreshStoreRepository({
      refreshRepository: () => api("POST", "/api/plugins/store/refresh"),
      reloadStore: async () => {
        list.store.loaded = false;
        await loadList("store", true);
      },
    });
    toast(t("plugins.plugin_repository_refreshed"));
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  } finally {
    storeRefreshing.value = false;
  }
}
async function updateAllStorePlugins() {
  try {
    const result = await api("POST", "/api/plugins/store/update-all") as {
      updated?: unknown[];
      failed?: Array<{ name?: string; code?: string; args?: Record<string, unknown> }>;
      eligibleCount?: number;
      eligible?: number;
      restartRequired?: boolean;
    };
    const updated = Array.isArray(result?.updated) ? result.updated.length : 0;
    const failedItems = Array.isArray(result?.failed) ? result.failed : [];
    const failed = failedItems.length;
    if (result?.restartRequired === true || updated > 0) shell.markRestartRequired("plugins.update-all");
    const eligibleValue = Number.isFinite(Number(result?.eligibleCount))
      ? Number(result.eligibleCount)
      : Number.isFinite(Number(result?.eligible))
        ? Number(result.eligible)
        : updated + failed;
    const failureDetails = failedItems.slice(0, 5).map(item => {
      const message = t(`api.error.${item.code || "internal_error"}`, item.args || {}, item.code || t("plugins.update_failed"));
      return `${item.name || t("plugins.unknown_plugin")}: ${message}`;
    });
    const summary = eligibleValue === 0
      ? t("plugin.store.update_all_none")
      : t("plugin.store.update_all_done", { updated, failed });
    toast(failureDetails.length ? `${summary}\n${failureDetails.join("\n")}` : summary, failed > 0 ? "error" : "info");
    list.local.loaded = false;
    list.store.loaded = false;
    await loadList("store", true);
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
function onEscape(event: KeyboardEvent) {
  if (event.key === "Escape" && filterOpen.value) filterOpen.value = false;
}

onMounted(() => {
  setTopbarTitle(t("common.plugin"));
  window.addEventListener("keydown", onEscape);
  void loadList("local");
});
onBeforeUnmount(() => {
  window.removeEventListener("keydown", onEscape);
  requestSerial.value += 1;
  listSerial.value += 1;
});
</script>

<template>
  <main id="view" class="view-root" data-testid="main-view">
    <NxpPageHeader
      class="plugin-page-head"
      :description="activeTab === 'store' ? t('plugins.page.help') : t('plugins.installed.help')"
    >
      <template #eyebrow>{{ t("common.plugin") }}</template>
      <template #title>{{ t("common.plugin") }}</template>
      <template #actions>
        <NxpTabs
          class="plugin-page-tabs"
          :model-value="activeTab"
          :tabs="tabs"
          :aria-label="t('plugins.plugin_view')"
          @change="switchTab"
        />
      </template>
    </NxpPageHeader>
    <div class="plugin-browser" :class="{ 'detail-visible': detailVisibleMobile }" data-testid="plugin-browser">
      <div class="plugin-list-column">
        <div class="plugin-search-toolbar">
          <label class="plugin-search">
            <span class="sr-only">{{ t("plugins.search.placeholder") }}</span>
            <input
              v-model="currentView.query"
              type="search"
              :placeholder="t('plugins.search.placeholder')"
              :aria-label="t('plugins.search.placeholder')"
              data-testid="plugin-search"
              autocomplete="off"
            >
          </label>
          <div class="plugin-filter-wrap">
            <button
              class="plugin-filter-trigger"
              :class="{ 'is-active': isPluginViewStateActive(currentView) }"
              type="button"
              data-testid="plugin-filter"
              aria-haspopup="dialog"
              :aria-expanded="filterOpen"
              aria-controls="plugin-filter-popover"
              @click="filterOpen = !filterOpen"
            >
              <NxpIcon name="filter" /><span>{{ t("plugins.filter") }}</span>
              <span v-if="isPluginViewStateActive(currentView)" class="plugin-filter-status">{{ t("common.set") }}</span>
            </button>
            <FilterPopover
              :open="filterOpen"
              :view="currentView"
              @close="filterOpen = false"
              @update="updateFilter"
              @reset="resetFilter"
            />
          </div>
        </div>
        <PluginList
          :tab="activeTab"
          :loading="currentList.loading"
          :loaded="currentList.loaded"
          :error="currentList.error"
          :available="currentList.available"
          :stale="currentList.stale"
          :plugins="visiblePlugins"
          :selected-name="selected[activeTab]"
          @select="selectPlugin"
        />
      </div>
      <PluginDetail
        :tab="activeTab"
        :detail-visible-mobile="detailVisibleMobile"
        :loading="detail.loading"
        :error="detail.error"
        :plugin="detail.data"
        :fetched-at="currentList.fetchedAt"
        :refreshing="storeRefreshing"
        @back-to-list="detailVisibleMobile = false"
        @action="runPluginAction"
        @update-all="updateAllStorePlugins"
        @refresh="refreshStoreAsync"
      />
    </div>
  </main>
</template>
