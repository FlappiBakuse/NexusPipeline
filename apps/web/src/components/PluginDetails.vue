<script setup lang="ts">
import { computed, defineAsyncComponent } from 'vue';
import { locale, t } from '../app/locale';
import type { CatalogEntry, ExtensionState } from '../control/extensions';
import type { ExtensionMetadata } from '../control/extension-metadata';
type PluginDetailsInput = { name: string; summary: string; kind: string; version: string; metadata?: ExtensionMetadata; candidate?: CatalogEntry; installed?: ExtensionState };
import { releaseDocument, type ExtensionDetails } from '../control/extensions';
import { metadataText } from '../control/extension-metadata';
const MarkdownDocument = defineAsyncComponent(() => import('./MarkdownDocument.vue'));
const props = defineProps<{
  row: PluginDetailsInput;
  details?: ExtensionDetails;
  loading: boolean;
  error: string;
  status: string;
  tone: string;
  kindLabel: string;
}>();
const readme = computed(() => releaseDocument(props.details, 'README', locale.value));
const changelog = computed(() => releaseDocument(props.details, 'CHANGELOG', locale.value));
const metadata = computed(() => props.row.metadata);
const text = computed(() => metadata.value && metadataText(metadata.value, locale.value));
const facts = computed(() => [
  [
    '版本',
    props.details?.packageVersion
      ? 'v' + props.details.packageVersion
      : props.row.version
        ? 'v' + props.row.version
        : ''
  ],
  ['创建时间', metadata.value?.createdAt ? new Date(metadata.value.createdAt).toLocaleString(locale.value) : ''],
  ['更新时间', metadata.value?.updatedAt ? new Date(metadata.value.updatedAt).toLocaleString(locale.value) : ''],
  ['适用项目', text.value?.gameName || t('通用')],
  ['插件类型', props.kindLabel],
  [
    '最低宿主版本',
    props.details?.manifest.minHostVersion
      ? 'v' + props.details.manifest.minHostVersion
      : props.row.candidate?.minHostVersion
        ? 'v' + props.row.candidate.minHostVersion
        : ''
  ],
  ['作者', metadata.value?.author || ''],
  ['标签', metadata.value?.tags.join(' · ') || '']
]);
</script>
<template>
  <nexus-scroll-area
    class="plugin-details"
    :label="t('插件详情')"
    :aria-label="t('插件详情')"
    :aria-busy="loading"
  >
    <header>
      <div class="badges">
        <nexus-badge :tone="row.kind === 'data-specialized' ? 'info' : 'neutral'" :label="kindLabel" />
        <nexus-badge :tone="tone" :label="status" />
      </div>
      <div class="detail-actions"><slot name="actions" /></div>
      <h3>{{ row.name }}</h3>
    </header>
    <p v-if="row.summary" class="plugin-summary">{{ row.summary }}</p>
    <dl class="plugin-facts">
      <div v-for="fact in facts" :key="fact[0]">
        <dt>{{ t(fact[0]) }}</dt>
        <dd>{{ fact[1] || t('未提供') }}</dd>
      </div>
    </dl>
    <p v-if="loading && !details" role="status">{{ t('正在验证发行文档…') }}</p>
    <p v-if="error" class="detail-error" role="alert">{{ t(error) }}</p>
    <section class="plugin-document">
      <h4>README</h4>
      <MarkdownDocument v-if="readme" :text="readme" />
      <p v-else-if="!loading" class="empty-document">{{ t('没有随包发行文档') }}</p>
    </section>
    <section class="plugin-document plugin-changelog">
      <h4>{{ t('更新记录') }}</h4>
      <MarkdownDocument v-if="changelog" :text="changelog" />
      <slot v-else name="history">
        <p class="empty-document">{{ t('暂无更新记录') }}</p>
      </slot>
    </section>
  </nexus-scroll-area>
</template>
<style scoped>
.plugin-details {
  min-height: 0;
  height: 100%;
  overflow: hidden;
  padding: clamp(18px, 2.5vw, 28px);
  border: 1px solid var(--border);
  border-radius: 16px;
  background: var(--content-card);
}
header {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: start;
  gap: 10px 12px;
}
.badges {
  grid-row: 1;
  grid-column: 1;
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.detail-actions {
  grid-column: 2;
  grid-row: 1 / span 2;
  display: flex;
  gap: 8px;
}
h3 {
  grid-column: 1;
  grid-row: 2;
  margin: 0;
  font-size: clamp(20px, 2.3vw, 28px);
  line-height: 1.35;
  font-weight: 700;
  overflow-wrap: anywhere;
}
.plugin-summary {
  margin: 18px 0 22px;
  font-size: 13px;
  line-height: 1.8;
  color: var(--muted);
}
.plugin-facts {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  column-gap: 18px;
  margin: 24px 0 16px;
  border-top: 1px solid var(--border);
}
.plugin-facts > div {
  min-height: 63px;
  padding: 11px 0 10px;
  border-bottom: 1px solid var(--border);
  min-width: 0;
}
dt {
  margin-bottom: 3px;
  color: var(--faint);
  font-size: 11px;
}
dd {
  margin: 0;
  font-size: 13px;
  overflow-wrap: anywhere;
}
.plugin-document h4 {
  margin: 16px 0 10px;
  font-size: 14px;
  line-height: 1.5;
  font-weight: 700;
}
.plugin-changelog {
  margin-top: 20px;
}
.empty-document {
  margin: 0;
  font-size: 12px;
  color: var(--muted);
}
.detail-error {
  color: var(--bad);
  font-size: 12px;
}
@media (max-width: 719px) {
  header {
    gap: 6px 12px;
  }
  h3 {
    grid-row: 1;
  }
  .badges {
    grid-row: 2;
  }
  .plugin-facts {
    grid-template-columns: minmax(0, 1fr);
  }
}
@media (max-width: 600px) {
  .detail-actions :deep(button) {
    min-height: 44px;
  }
}
</style>
