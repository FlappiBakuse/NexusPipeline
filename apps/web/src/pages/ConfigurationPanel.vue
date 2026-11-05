<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { t } from '../app/locale';
import { configurationOverview, type ConfigurationOverview } from './configuration-types';
import ValueEditorPanel from './ValueEditorPanel.vue';
import NativeEditorPanel from './NativeEditorPanel.vue';
import AdapterEditorPanel from './AdapterEditorPanel.vue';
import ConfigurationImport from './ConfigurationImport.vue';
import './configuration-messages';
import './configuration.css';

const props = defineProps<{ store: AppStore; bindingId: string; visible: boolean }>();
const emit = defineEmits<{ close: [] }>();
const overview = ref<ConfigurationOverview | null>(null);
const mode = ref<'value' | 'native' | 'adapter'>('value');
const busy = ref(false);
const error = ref('');
let owner: AbortController | undefined;
const nativeAllowed = computed(() => props.store.can('configuration.native.open'));
async function load(preserveMode = false): Promise<void> {
  owner?.abort();
  const next = new AbortController();
  owner = next;
  overview.value = null;
  error.value = '';
  busy.value = true;
  try {
    const value = await props.store.client.read(
      '/control/v1/configuration/bindings/' + props.bindingId + '/resources',
      configurationOverview,
      next.signal
    );
    next.signal.throwIfAborted();
    overview.value = value;
    if (
      !preserveMode ||
      (mode.value === 'value' && value.kind !== 'generic') ||
      (mode.value === 'adapter' && value.kind !== 'specialized')
    )
      mode.value = value.kind === 'generic' ? 'value' : 'adapter';
  } catch (reason) {
    if (!next.signal.aborted) {
      props.store.report(reason);
      error.value = reason instanceof Error ? reason.message : '配置不可用';
    }
  } finally {
    if (owner === next) busy.value = false;
  }
}
watch(
  () => [props.bindingId, props.visible],
  () => {
    if (props.visible) void load();
    else owner?.abort();
  },
  { immediate: true }
);
onBeforeUnmount(() => owner?.abort());
</script>
<template>
  <nexus-modal :open.prop="visible" :title="t('账号配置')" size="wide" @nexus-close="emit('close')">
    <div class="config-panel">
      <p v-if="busy" role="status">{{ t('正在读取配置资源…') }}</p>
      <p v-if="error" role="alert">
        {{ t(error) }}
        <button @click="load(true)">{{ t('刷新资源') }}</button>
      </p>
      <template v-if="overview">
        <ConfigurationImport :store="store" :overview="overview" @changed="load(true)" />
        <nav class="config-tabs" :aria-label="t('账号配置')">
          <button
            v-if="overview.kind === 'generic'"
            :aria-current="mode === 'value' ? 'page' : undefined"
            @click="mode = 'value'"
          >
            {{ t('配置值') }}
          </button>
          <button
            v-if="nativeAllowed"
            :aria-current="mode === 'native' ? 'page' : undefined"
            @click="mode = 'native'"
          >
            {{ t('原生编辑器') }}
          </button>
          <button
            v-if="overview.kind === 'specialized'"
            :aria-current="mode === 'adapter' ? 'page' : undefined"
            @click="mode = 'adapter'"
          >
            {{ t('专项配置') }}
          </button>
        </nav>
        <div v-if="!overview.resources.length" role="status">{{ t('暂无配置资源。') }}</div>
        <ValueEditorPanel
          v-else-if="mode === 'value' && overview.kind === 'generic'"
          :store="store"
          :overview="overview"
          :visible="visible"
          @changed="load(true)"
        />
        <NativeEditorPanel
          v-else-if="mode === 'native' && nativeAllowed"
          :store="store"
          :overview="overview"
          :visible="visible"
          @changed="load(true)"
        />
        <AdapterEditorPanel
          v-else-if="mode === 'adapter' && overview.kind === 'specialized'"
          :store="store"
          :overview="overview"
          :visible="visible"
          @changed="load(true)"
        />
      </template>
    </div>
    <div slot="footer" class="row">
      <button @click="emit('close')">{{ t('暂时关闭') }}</button>
    </div>
  </nexus-modal>
</template>
