<script setup lang="ts">
import { t } from "../../../platform/i18n";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpEmptyState from "../../../ui/primitives/NxpEmptyState.vue";
import NxpLoadingState from "../../../ui/composites/NxpLoadingState.vue";
import NxpScrollArea from "../../../ui/primitives/NxpScrollArea.vue";
import { pluginKindLabel, pluginKindTone, pluginStatusView, type PluginViewPlugin, type PluginViewTab } from "../utils/pluginStatusView";

/** 插件列表栏：加载态、仓库不可用、错误与列表行。选中与加载由页面驱动。 */

const props = defineProps<{
  tab: PluginViewTab;
  loading: boolean;
  loaded: boolean;
  error: string;
  available: boolean;
  stale: boolean;
  plugins: PluginViewPlugin[];
  selectedName: string;
}>();

const emit = defineEmits<{ select: [plugin: PluginViewPlugin] }>();

function listTestId() {
  return props.tab === "store" ? "plugin-store-list" : "plugin-local-list";
}
function loadingTestId() {
  return props.tab === "store" ? "plugin-store-loading" : "plugin-local-loading";
}
function rowTestId() {
  return props.tab === "store" ? "plugin-store-row" : "plugin-local-row";
}
function description(plugin: PluginViewPlugin) {
  return plugin.description || t(props.tab === "store" ? "plugins.official_plugin" : "plugins.local_extension");
}
</script>

<template>
  <NxpScrollArea class="plugin-list-pane" :data-testid="listTestId()" :aria-label="tab === 'store' ? t('plugins.plugin_repository') : t('plugins.local_plugins')">
    <NxpLoadingState
      v-if="loading && !loaded"
      :title="tab === 'store' ? t('plugins.loading_plugin_repository') : t('plugins.loading_local_plugins')"
      :description="tab === 'store' ? t('plugins.catalog.loading') : t('plugins.reading_local_plugin_status')"
      :test-id="loadingTestId()"
      :aria-label="tab === 'store' ? t('plugins.loading_plugin_repository') : t('plugins.loading_local_plugins')"
    />
    <div v-else-if="tab === 'store' && !available" class="plugin-store-unavailable-message">
      <strong>{{ t("plugins.plugin_repository_unavailable") }}</strong>
      <span>{{ error || t("plugins.catalog.network_help") }}</span>
    </div>
    <div v-else-if="error && !plugins.length" class="empty">
      <strong>{{ t("plugins.load.local_failed") }}</strong>
      <span>{{ error }}</span>
    </div>
    <div v-else class="plugin-list" role="listbox">
      <div v-if="stale" class="callout callout-warning" data-testid="plugin-store-stale">
        {{ t("plugin.store.stale", {}, "The repository is temporarily unavailable. Showing the cached catalog.") }}
      </div>
      <button
        v-for="plugin in plugins"
        :key="plugin.name"
        class="plugin-list-item"
        :class="{ 'is-selected': selectedName === plugin.name }"
        type="button"
        role="option"
        :aria-selected="selectedName === plugin.name"
        :data-testid="rowTestId()"
        @click="emit('select', plugin)"
      >
        <span class="plugin-list-item-main">
          <span class="plugin-name-line">
            <strong class="plugin-name-scroll" tabindex="0" :title="plugin.displayName || plugin.name">
              <span class="plugin-name-scroll-inner">{{ plugin.displayName || plugin.name }}</span>
            </strong>
          </span>
          <span class="muted plugin-list-description">{{ description(plugin) }}</span>
        </span>
        <span class="plugin-list-item-meta">
          <NxpBadge :tone="pluginKindTone(plugin)">{{ pluginKindLabel(plugin, t) }}</NxpBadge>
          <NxpBadge :tone="pluginStatusView(plugin, tab, t).tone">{{ pluginStatusView(plugin, tab, t).label }}</NxpBadge>
          <NxpBadge tone="muted">{{ plugin.version ? `v${plugin.version}` : t("plugins.version_not_specified") }}</NxpBadge>
        </span>
      </button>
      <NxpEmptyState v-if="!plugins.length" :title="t('plugins.no_matching_plugins')" :description="t('plugins.search.no_match_help')" />
    </div>
  </NxpScrollArea>
</template>
