<script setup lang="ts">
import { t } from "../../../platform/i18n";
import { renderMarkdown } from "../../../platform/markdown";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpLoadingState from "../../../ui/composites/NxpLoadingState.vue";
import NxpScrollArea from "../../../ui/primitives/NxpScrollArea.vue";
import { authorName, pluginKindLabel, pluginKindTone, pluginStatusView, pluginTags, storeActionNotice, storeActions, type PluginViewPlugin, type PluginViewTab } from "../utils/pluginStatusView";

/** 插件详情栏：README、changelog、元数据与商店动作。页面负责请求与动作执行。 */

const props = defineProps<{
  tab: PluginViewTab;
  detailVisibleMobile: boolean;
  loading: boolean;
  error: string;
  plugin: PluginViewPlugin | null;
  fetchedAt: string;
  refreshing: boolean;
}>();

const emit = defineEmits<{
  backToList: [];
  action: [action: string, name: string];
  updateAll: [];
  refresh: [];
}>();

function actions(plugin: PluginViewPlugin) {
  return storeActions(plugin, t);
}
</script>

<template>
  <div class="plugin-detail-column">
    <button v-if="detailVisibleMobile" class="plugin-detail-back ghost" type="button" @click="emit('backToList')">
      {{ t("plugins.back_to_plugin_list") }}
    </button>
    <NxpScrollArea class="plugin-detail-pane" data-testid="plugin-detail" :aria-label="t('plugins.plugin_details')">
      <NxpLoadingState
        v-if="loading && !plugin"
        class="plugin-detail-loading"
        :title="t('plugins.loading_plugin_details')"
        :description="t('plugins.detail.readme_loading')"
        :aria-label="t('plugins.loading_plugin_details')"
      />
      <div v-else-if="error && !plugin" class="empty">
        <strong>{{ t("plugins.load.detail_failed") }}</strong>
        <span>{{ error }}</span>
      </div>
      <div v-else-if="!plugin" class="empty">
        <strong>{{ t("plugins.select_a_plugin") }}</strong>
        <span>{{ t("plugins.selection.help") }}</span>
      </div>
      <div v-else class="plugin-detail-content">
        <div class="plugin-detail-head">
          <div class="plugin-detail-title">
            <div>
              <NxpBadge :tone="pluginKindTone(plugin)">{{ pluginKindLabel(plugin, t) }}</NxpBadge>
              <NxpBadge :tone="pluginStatusView(plugin, tab, t).tone">{{ pluginStatusView(plugin, tab, t).label }}</NxpBadge>
            </div>
            <h3 class="plugin-detail-name-scroll" tabindex="0" :title="plugin.displayName || plugin.name">
              <span class="plugin-detail-name-scroll-inner">{{ plugin.displayName || plugin.name }}</span>
            </h3>
          </div>
          <div class="plugin-detail-actions">
            <NxpButton v-if="tab === 'local'" class="tertiary" @click="emit('action', plugin.configuredEnabled ? 'disable' : 'enable', String(plugin.name))">
              {{ plugin.configuredEnabled ? t("plugins.disable_plugin") : t("plugins.enable_plugin") }}
            </NxpButton>
            <template v-else>
              <span v-if="storeActionNotice(plugin, t)" class="muted">{{ storeActionNotice(plugin, t) }}</span>
              <NxpButton v-for="item in actions(plugin)" :key="item.action" :class="item.tone" @click="emit('action', item.action, String(plugin.name))">{{ item.label }}</NxpButton>
              <span v-if="!storeActionNotice(plugin, t) && !actions(plugin).length" class="muted">{{ t("common.already_up_to_date") }}</span>
            </template>
          </div>
        </div>
        <p class="plugin-detail-description">{{ plugin.description || t("plugins.no_description") }}</p>
        <div v-if="tab === 'local' && plugin.runtimeErrorCode" class="field-error-message">{{ t("plugin.store.runtime_error") }}</div>
        <div v-if="plugin.installed && plugin.installedVersion && plugin.installedVersion !== plugin.version" class="callout callout-warning">
          {{ t("plugins.version.installed", { version: plugin.installedVersion }) }}
        </div>
        <dl class="plugin-detail-meta" :class="{ 'has-homepage': plugin.homepage }">
          <div><dt>{{ t("plugins.version") }}</dt><dd>{{ plugin.version ? `v${plugin.version}` : t("plugins.version_not_specified") }}</dd></div>
          <div><dt>{{ t("plugins.created_at") }}</dt><dd>{{ plugin.createdAt || t("plugins.not_provided") }}</dd></div>
          <div><dt>{{ t("plugins.updated_at") }}</dt><dd>{{ plugin.updatedAt || t("plugins.not_provided") }}</dd></div>
          <div><dt>{{ t("plugins.supported_project") }}</dt><dd>{{ plugin.gameName || t("common.general") }}</dd></div>
          <div><dt>{{ t("plugins.plugin_type") }}</dt><dd>{{ pluginKindLabel(plugin, t) }}</dd></div>
          <div v-if="tab === 'store' && plugin.minHostVersion && plugin.minHostVersion !== '0.0.0'">
            <dt>{{ t("plugins.minimum_host_version") }}</dt><dd>v{{ plugin.minHostVersion }}</dd>
          </div>
          <div><dt>{{ t("plugins.author") }}</dt><dd>{{ authorName(plugin, t) }}</dd></div>
          <div>
            <dt>{{ t("plugins.tags") }}</dt>
            <dd>
              <span v-if="pluginTags(plugin).length" class="plugin-detail-tags">
                <NxpBadge v-for="tag in pluginTags(plugin)" :key="tag" tone="muted">{{ tag }}</NxpBadge>
              </span>
              <span v-else class="muted">{{ t("plugins.not_provided") }}</span>
            </dd>
          </div>
          <div v-if="plugin.homepage" class="plugin-detail-homepage-row">
            <dt>{{ t("plugins.project_homepage") }}</dt>
            <dd><a :href="String(plugin.homepage)" target="_blank" rel="noopener noreferrer">{{ t("plugins.open_homepage") }}</a></dd>
          </div>
        </dl>
        <section class="plugin-detail-section">
          <h4>README</h4>
          <div v-if="plugin.readmeErrorCode" class="callout callout-warning">{{ t('plugin.store.readme_error', {}, 'README 加载失败') }}</div>
          <div v-else-if="plugin.readmeAvailable === true && plugin.readmeMarkdown" class="plugin-readme" v-html="renderMarkdown(plugin.readmeMarkdown)"></div>
          <div v-else class="empty compact-empty"><span>{{ plugin.hasReadme ? t("plugins.readme.empty") : t("plugins.no_readme") }}</span></div>
        </section>
        <section class="plugin-detail-section">
          <h4>{{ t("plugins.changelog") }}</h4>
          <div v-if="Array.isArray(plugin.changelog) && plugin.changelog.length" class="plugin-detail-changelog">
            <article v-for="entry in plugin.changelog" :key="`${entry.version}-${entry.date}`" class="plugin-changelog-entry">
              <div class="plugin-changelog-version"><strong>v{{ entry.version }}</strong><span class="muted">{{ entry.date }}</span></div>
              <ul><li v-for="item in entry.items || []" :key="item">{{ item }}</li></ul>
            </article>
          </div>
          <div v-else class="empty compact-empty"><span>{{ t("plugins.no_changelog_entries") }}</span></div>
        </section>
      </div>
    </NxpScrollArea>
    <div v-if="props.tab === 'store'" class="plugin-browser-footer">
      <span class="muted">{{ fetchedAt ? t("plugins.catalog.updated", { time: fetchedAt }) : "" }}</span>
      <span class="plugin-browser-footer-actions">
        <NxpButton class="primary" data-testid="plugin-store-update-all" @click="emit('updateAll')">{{ t("plugin.store.update_all", {}, "Update all plugins") }}</NxpButton>
        <NxpButton class="tertiary" data-testid="plugin-store-refresh" :disabled="refreshing" @click="emit('refresh')">{{ t("plugins.refresh_repository") }}</NxpButton>
      </span>
    </div>
  </div>
</template>
