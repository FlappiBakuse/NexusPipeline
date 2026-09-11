<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from "vue";
import { api, isAbortError } from "@legacy/core/api.js";
import { filterAndSortPlugins, defaultPluginViewState, isPluginViewStateActive } from "@legacy/core/plugin-list.js";
import { renderMarkdown } from "@legacy/core/markdown.js";
import { t } from "@legacy/core/i18n.js";
import { setTopbarTitle, toast } from "@legacy/core/ui.js";
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpLoadingState from "../../ui/composites/NxpLoadingState.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";

type Tab = "local" | "store";
type Plugin = Record<string, any> & { name: string; displayName?: string; description?: string; kind?: string; version?: string };
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

const currentList = computed(() => list[activeTab.value]);
const currentView = computed(() => view[activeTab.value]);
const visiblePlugins = computed(() => filterAndSortPlugins(currentList.value.plugins, currentView.value) as Plugin[]);
const detail = computed(() => details[activeTab.value]);
const detailVisibleMobile = ref(false);

function pluginKindLabel(plugin: Plugin) { return t(plugin.kind === "data-specialized" ? "common.specialized_plugin" : "plugins.general_plugin"); }
function pluginKindTone(plugin: Plugin): "muted" | "blue" { return plugin.kind === "data-specialized" ? "blue" : "muted"; }
function runtimeLabel(plugin: Plugin) {
  if ((plugin.state || (plugin.runtimeEnabled ? "Active" : "Disabled")) === "Active") return plugin.configuredEnabled ? t("common.running") : t("plugins.running_restart_required");
  if (plugin.state === "InitFailed") return t("plugins.initialization_failed");
  if (plugin.state === "Incompatible") return t("plugins.incompatible_api");
  return plugin.configuredEnabled ? t("plugins.restart_required") : t("common.disabled");
}
function runtimeTone(plugin: Plugin): "ok" | "bad" | "muted" {
  if ((plugin.state || (plugin.runtimeEnabled ? "Active" : "Disabled")) === "Active") return "ok";
  if (["InitFailed", "InitTimedOut", "StartTimedOut", "StopTimedOut", "Incompatible"].includes(plugin.state)) return "bad";
  return "muted";
}
function storeStatusLabel(plugin: Plugin) {
  return t(({ "not-installed": "plugins.not_installed", installed: "plugins.installed", "update-available": "plugins.update_available", pending: "plugins.restart_required", incompatible: "plugins.incompatible_with_host", unlisted: "plugins.not_listed_in_repository" } as Record<string, string>)[plugin.status] || "plugins.available");
}
function storeTone(plugin: Plugin): "ok" | "warn" | "bad" | "muted" {
  if (plugin.status === "incompatible") return "bad";
  if (["update-available", "pending"].includes(plugin.status)) return "warn";
  if (plugin.status === "installed") return "ok";
  return "muted";
}
function storeActionNotice(plugin: Plugin) {
  if (plugin.status === "pending") {
    return t("plugins.store.pending_restart", {
      action: t(plugin.pendingAction === "uninstall" ? "plugins.uninstall" : "plugins.install"),
      version: plugin.pendingVersion || plugin.version || "",
      restart: t("plugins.effective_after_restart"),
    });
  }
  if (plugin.compatible === false) return t("plugin.store.incompatible", {}, "The current host version is incompatible");
  return "";
}
function storeActions(plugin: Plugin) {
  if (storeActionNotice(plugin)) return [] as Array<{ action: string; label: string; tone: string }>;
  const result: Array<{ action: string; label: string; tone: string }> = [];
  if (plugin.status === "unlisted") {
    result.push({ action: "uninstall", label: t("plugins.uninstall_plugin"), tone: "danger" });
    return result;
  }
  if (!plugin.installed) {
    result.push({ action: "install", label: t("plugins.install_plugin"), tone: "primary" });
  } else if (plugin.updateAvailable) {
    result.push({ action: "update", label: t("plugins.update_plugin"), tone: "primary" });
  }
  if (plugin.installed && (!plugin.installedName || plugin.installedName === plugin.name)) {
    result.push({ action: "uninstall", label: t("plugins.uninstall_plugin"), tone: "tertiary" });
  }
  return result;
}
function statusText(plugin: Plugin, tab: Tab) { return tab === "store" ? storeStatusLabel(plugin) : runtimeLabel(plugin); }
function statusTone(plugin: Plugin, tab: Tab) { return tab === "store" ? storeTone(plugin) : runtimeTone(plugin); }
function authorName(plugin: Plugin) {
  return Array.isArray(plugin.authors) && plugin.authors.length
    ? plugin.authors.map((author: any) => author?.name || t("plugins.unknown_author")).join(", ")
    : t("plugins.not_provided");
}
function tags(plugin: Plugin) { return Array.isArray(plugin.tags) ? plugin.tags.map(String).filter(Boolean) : []; }

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
    const data = await api("GET", tab === "store" ? "/api/plugins/store" : "/api/plugins");
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

function switchTab(tab: Tab) {
  filterOpen.value = false;
  activeTab.value = tab;
  detailVisibleMobile.value = false;
  void loadList(tab);
}
function selectPlugin(plugin: Plugin) {
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
    await api("POST", activeTab.value === "store" ? `/api/plugins/store/${encodeURIComponent(name)}/${action}` : `/api/plugins/${encodeURIComponent(name)}/${action}`);
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
async function updateAllStorePlugins() {
  try {
    const result = await api("POST", "/api/plugins/store/update-all") as {
      updated?: unknown[];
      failed?: Array<{ name?: string; code?: string; args?: Record<string, unknown> }>;
      eligibleCount?: number;
      eligible?: number;
    };
    const updated = Array.isArray(result?.updated) ? result.updated.length : 0;
    const failedItems = Array.isArray(result?.failed) ? result.failed : [];
    const failed = failedItems.length;
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
    <header class="page-head plugin-page-head"><div class="page-head-copy"><div class="eyebrow">{{ t("common.plugin") }}</div><h2>{{ t("common.plugin") }}</h2><p class="page-kicker">{{ activeTab === "store" ? t("plugins.page.help") : t("plugins.installed.help") }}</p></div><div class="page-head-actions"><div class="plugin-tabs plugin-page-tabs" role="tablist" :aria-label="t('plugins.plugin_view')"><button :class="activeTab === 'local' ? 'primary' : 'tertiary'" type="button" role="tab" :aria-selected="activeTab === 'local'" data-testid="plugin-local-tab" @click="switchTab('local')">{{ t("plugins.local_plugins") }}</button><button :class="activeTab === 'store' ? 'primary' : 'tertiary'" type="button" role="tab" :aria-selected="activeTab === 'store'" data-testid="plugin-store-tab" @click="switchTab('store')">{{ t("plugins.plugin_repository") }}</button></div></div></header>
    <div class="plugin-browser" :class="{ 'detail-visible': detailVisibleMobile }" data-testid="plugin-browser">
      <div class="plugin-list-column">
        <div class="plugin-search-toolbar"><label class="plugin-search"><span class="sr-only">{{ t("plugins.search.placeholder") }}</span><input v-model="currentView.query" type="search" :placeholder="t('plugins.search.placeholder')" :aria-label="t('plugins.search.placeholder')" data-testid="plugin-search" autocomplete="off"></label><div class="plugin-filter-wrap"><button class="plugin-filter-trigger" :class="{ 'is-active': isPluginViewStateActive(currentView) }" type="button" data-testid="plugin-filter" aria-haspopup="dialog" :aria-expanded="filterOpen" aria-controls="plugin-filter-popover" @click="filterOpen = !filterOpen"><NxpIcon name="filter" /><span>{{ t("plugins.filter") }}</span><span v-if="isPluginViewStateActive(currentView)" class="plugin-filter-status">{{ t("common.set") }}</span></button><div v-if="filterOpen" id="plugin-filter-popover" class="plugin-filter-popover" role="dialog" :aria-label="t('plugins.plugin_filters_and_sorting')"><fieldset class="plugin-filter-section"><legend>{{ t("plugins.plugin_type") }}</legend><div class="plugin-filter-options" role="radiogroup"><button v-for="[key, label] in [['all', t('common.all')], ['managed-code', t('plugins.general_plugin')], ['data-specialized', t('common.specialized_plugin')]]" :key="key" class="plugin-filter-option" :class="{ 'is-selected': currentView.kind === key }" type="button" role="radio" :aria-checked="currentView.kind === key" :data-testid="`plugin-filter-kind-${key}`" @click="updateFilter({ kind: key })">{{ label }}<NxpIcon v-if="currentView.kind === key" name="check" class-name="plugin-filter-option-check" /></button></div></fieldset><fieldset class="plugin-filter-section"><legend>{{ t("plugins.sort") }}</legend><div class="plugin-filter-options" role="radiogroup"><button v-for="[key, label] in [['name', t('plugins.name_initial_or_pinyin')], ['createdAt', t('plugins.created_at')], ['updatedAt', t('plugins.updated_at')]]" :key="key" class="plugin-filter-option" :class="{ 'is-selected': currentView.sortBy === key }" type="button" role="radio" :aria-checked="currentView.sortBy === key" :data-testid="`plugin-filter-sort-${key}`" @click="updateFilter({ sortBy: key })">{{ label }}<NxpIcon v-if="currentView.sortBy === key" name="check" class-name="plugin-filter-option-check" /></button></div><div class="plugin-filter-direction" role="radiogroup"><button v-for="direction in ['asc', 'desc']" :key="direction" class="plugin-filter-option" :class="{ 'is-selected': currentView.direction === direction }" type="button" role="radio" :aria-checked="currentView.direction === direction" :data-testid="`plugin-filter-direction-${direction}`" @click="updateFilter({ direction })">{{ direction === 'asc' ? t('plugins.ascending') : t('plugins.descending') }}<NxpIcon v-if="currentView.direction === direction" name="check" class-name="plugin-filter-option-check" /></button></div></fieldset><button v-if="isPluginViewStateActive(currentView)" class="plugin-filter-reset ghost" type="button" data-testid="plugin-filter-reset" @click="resetFilter">{{ t("plugins.reset") }}</button></div></div></div>
        <section class="plugin-list-pane" :data-testid="activeTab === 'store' ? 'plugin-store-list' : 'plugin-local-list'"><NxpLoadingState v-if="currentList.loading && !currentList.loaded" :title="activeTab === 'store' ? t('plugins.loading_plugin_repository') : t('plugins.loading_local_plugins')" :description="activeTab === 'store' ? t('plugins.catalog.loading') : t('plugins.reading_local_plugin_status')" :test-id="activeTab === 'store' ? 'plugin-store-loading' : 'plugin-local-loading'" :aria-label="activeTab === 'store' ? t('plugins.loading_plugin_repository') : t('plugins.loading_local_plugins')" /><div v-else-if="activeTab === 'store' && !currentList.available" class="plugin-store-unavailable-message"><strong>{{ t("plugins.plugin_repository_unavailable") }}</strong><span>{{ currentList.error || t("plugins.catalog.network_help") }}</span></div><div v-else-if="currentList.error && !currentList.plugins.length" class="empty"><strong>{{ t("plugins.load.local_failed") }}</strong><span>{{ currentList.error }}</span></div><div v-else class="plugin-list" role="listbox"><div v-if="currentList.stale" class="callout callout-warning" data-testid="plugin-store-stale">{{ t("plugin.store.stale", {}, "The repository is temporarily unavailable. Showing the cached catalog.") }}</div><button v-for="plugin in visiblePlugins" :key="plugin.name" class="plugin-list-item" :class="{ 'is-selected': selected[activeTab] === plugin.name }" type="button" role="option" :aria-selected="selected[activeTab] === plugin.name" :data-testid="activeTab === 'store' ? 'plugin-store-row' : 'plugin-local-row'" @click="selectPlugin(plugin)"><span class="plugin-list-item-main"><span class="plugin-name-line"><strong class="plugin-name-scroll" tabindex="0">{{ plugin.displayName || plugin.name }}</strong></span><span class="muted plugin-list-description">{{ plugin.description || (activeTab === 'store' ? t('plugins.official_plugin') : t('plugins.local_extension')) }}</span></span><span class="plugin-list-item-meta"><NxpBadge :tone="pluginKindTone(plugin)">{{ pluginKindLabel(plugin) }}</NxpBadge><NxpBadge :tone="statusTone(plugin, activeTab)">{{ statusText(plugin, activeTab) }}</NxpBadge><NxpBadge tone="muted">{{ plugin.version ? `v${plugin.version}` : t("plugins.version_not_specified") }}</NxpBadge></span></button><NxpEmptyState v-if="!visiblePlugins.length" :title="t('plugins.no_matching_plugins')" :description="t('plugins.search.no_match_help')" /></div></section>
      </div>
      <div class="plugin-detail-column"><button v-if="detailVisibleMobile" class="plugin-detail-back ghost" type="button" @click="detailVisibleMobile = false">{{ t("plugins.back_to_plugin_list") }}</button><section class="plugin-detail-pane" data-testid="plugin-detail"><NxpLoadingState v-if="detail.loading && !detail.data" class="plugin-detail-loading" :title="t('plugins.loading_plugin_details')" :description="t('plugins.detail.readme_loading')" :aria-label="t('plugins.loading_plugin_details')" /><div v-else-if="detail.error && !detail.data" class="empty"><strong>{{ t("plugins.load.detail_failed") }}</strong><span>{{ detail.error }}</span></div><div v-else-if="!detail.data" class="empty"><strong>{{ t("plugins.select_a_plugin") }}</strong><span>{{ t("plugins.selection.help") }}</span></div><div v-else class="plugin-detail-content"><div class="plugin-detail-head"><div class="plugin-detail-title"><div><NxpBadge :tone="pluginKindTone(detail.data)">{{ pluginKindLabel(detail.data) }}</NxpBadge><NxpBadge :tone="statusTone(detail.data, activeTab)">{{ statusText(detail.data, activeTab) }}</NxpBadge></div><h3 class="plugin-detail-name-scroll" tabindex="0" :title="detail.data.displayName || detail.data.name"><span class="plugin-detail-name-scroll-inner">{{ detail.data.displayName || detail.data.name }}</span></h3></div><div class="plugin-detail-actions"><NxpButton v-if="activeTab === 'local'" class="tertiary" @click="runPluginAction(detail.data.configuredEnabled ? 'disable' : 'enable')">{{ detail.data.configuredEnabled ? t('plugins.disable_plugin') : t('plugins.enable_plugin') }}</NxpButton><template v-else><span v-if="storeActionNotice(detail.data)" class="muted">{{ storeActionNotice(detail.data) }}</span><NxpButton v-for="item in storeActions(detail.data)" :key="item.action" :class="item.tone" @click="runPluginAction(item.action, detail.data.name)">{{ item.label }}</NxpButton><span v-if="!storeActionNotice(detail.data) && !storeActions(detail.data).length" class="muted">{{ t("common.already_up_to_date") }}</span></template></div></div><p class="plugin-detail-description">{{ detail.data.description || t("plugins.no_description") }}</p><div v-if="activeTab === 'local' && detail.data.runtimeErrorCode" class="field-error-message">{{ t("plugin.store.runtime_error") }}</div><div v-if="detail.data.installed && detail.data.installedVersion && detail.data.installedVersion !== detail.data.version" class="callout callout-warning">{{ t("plugins.version.installed", { version: detail.data.installedVersion }) }}</div><dl class="plugin-detail-meta" :class="{ 'has-homepage': detail.data.homepage }"><div><dt>{{ t("plugins.version") }}</dt><dd>{{ detail.data.version ? `v${detail.data.version}` : t("plugins.version_not_specified") }}</dd></div><div><dt>{{ t("plugins.created_at") }}</dt><dd>{{ detail.data.createdAt || t("plugins.not_provided") }}</dd></div><div><dt>{{ t("plugins.updated_at") }}</dt><dd>{{ detail.data.updatedAt || t("plugins.not_provided") }}</dd></div><div><dt>{{ t("plugins.supported_project") }}</dt><dd>{{ detail.data.gameName || t("common.general") }}</dd></div><div><dt>{{ t("plugins.plugin_type") }}</dt><dd>{{ pluginKindLabel(detail.data) }}</dd></div><div v-if="activeTab === 'store' && detail.data.minHostVersion && detail.data.minHostVersion !== '0.0.0'"><dt>{{ t("plugins.minimum_host_version") }}</dt><dd>v{{ detail.data.minHostVersion }}</dd></div><div><dt>{{ t("plugins.author") }}</dt><dd>{{ authorName(detail.data) }}</dd></div><div><dt>{{ t("plugins.tags") }}</dt><dd><span v-if="tags(detail.data).length" class="plugin-detail-tags"><NxpBadge v-for="tag in tags(detail.data)" :key="tag" tone="muted">{{ tag }}</NxpBadge></span><span v-else class="muted">{{ t("plugins.not_provided") }}</span></dd></div><div v-if="detail.data.homepage" class="plugin-detail-homepage-row"><dt>{{ t("plugins.project_homepage") }}</dt><dd><a :href="detail.data.homepage" target="_blank" rel="noopener noreferrer">{{ t("plugins.open_homepage") }}</a></dd></div></dl><section class="plugin-detail-section"><h4>README</h4><div v-if="detail.data.readmeErrorCode" class="callout callout-warning">{{ t('plugin.store.readme_error', {}, 'README 加载失败') }}</div><div v-else-if="detail.data.readmeAvailable === true && detail.data.readmeMarkdown" class="plugin-readme" v-html="renderMarkdown(detail.data.readmeMarkdown)"></div><div v-else class="empty compact-empty"><span>{{ detail.data.hasReadme ? t("plugins.readme.empty") : t("plugins.no_readme") }}</span></div></section><section class="plugin-detail-section"><h4>{{ t("plugins.changelog") }}</h4><div v-if="Array.isArray(detail.data.changelog) && detail.data.changelog.length" class="plugin-detail-changelog"><article v-for="entry in detail.data.changelog" :key="`${entry.version}-${entry.date}`" class="plugin-changelog-entry"><div class="plugin-changelog-version"><strong>v{{ entry.version }}</strong><span class="muted">{{ entry.date }}</span></div><ul><li v-for="item in entry.items || []" :key="item">{{ item }}</li></ul></article></div><div v-else class="empty compact-empty"><span>{{ t("plugins.no_changelog_entries") }}</span></div></section></div></section><div v-if="activeTab === 'store'" class="plugin-browser-footer"><span class="muted">{{ currentList.fetchedAt ? t("plugins.catalog.updated", { time: currentList.fetchedAt }) : "" }}</span><span class="plugin-browser-footer-actions"><NxpButton class="primary" data-testid="plugin-store-update-all" @click="updateAllStorePlugins">{{ t("plugin.store.update_all", {}, "Update all plugins") }}</NxpButton><NxpButton class="tertiary" data-testid="plugin-store-refresh" @click="loadList('store', true)">{{ t("plugins.refresh_repository") }}</NxpButton></span></div></div>
    </div>
  </main>
</template>
