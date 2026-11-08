<script setup lang="ts">
import { scriptIcon } from './script-icons';
import { computed, onBeforeUnmount, watch, ref } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import { extensionList, type ExtensionState } from '../control/extensions';
import { metadataText } from '../control/extension-metadata';
import { t, locale, registerMessages } from '../app/locale';

registerMessages({ '由驱动插件编译并执行项目': 'Compile and run the project with the driver plugin' });
export type ScriptChoice = { extensionId: string; label: string; artifact: string; driver?: boolean };
const props = defineProps<{ store: AutomationStore; visible: boolean; active: boolean }>();
const emit = defineEmits<{ choose: [value?: ScriptChoice]; close: [] }>();
const installed = ref<ExtensionState[]>([]);
const choices = computed<ScriptChoice[]>(() => installed.value.filter(item => item.isInstalled && item.desiredEnabled &&
  item.activationId && !item.unavailableReason && item.capabilities.includes('task.adapt') && (item.kind === 'data-specialized' ||
    item.kind === 'managed-code' && item.capabilities.includes('source.compile') && item.capabilities.includes('execution.drive')))
  .map(item => ({ extensionId: item.extensionId, label: item.metadata ? metadataText(item.metadata, locale.value).displayName : item.artifactName || item.extensionId,
    driver: item.kind === 'managed-code', artifact: item.artifactName || item.extensionId })).sort((a, b) => Number(!!b.driver) - Number(!!a.driver) || a.artifact.localeCompare(b.artifact, 'en')));
const loading = ref(false);
const error = ref('');
const ready = ref(false);
let owner: AbortController | undefined;
const instance = props.store.state.capabilities?.instanceId;

watch(() => [props.active, props.visible, props.store.state.settings?.saved.revision] as const, async ([active]) => {
  if (!active || !props.store.can('extensions.read')) return;
  owner?.abort();
  const current = new AbortController();
  owner = current;
  loading.value = true;
  error.value = '';
  try {
    const catalog = await props.store.client.read('/control/v1/extensions', extensionList, current.signal);
    if (!current.signal.aborted && props.store.state.capabilities?.instanceId === instance) {
      installed.value = catalog.items;
      ready.value = true;
    }
  } catch (reason) {
    if (!current.signal.aborted) error.value = '脚本类型加载失败，请重新打开。';
  } finally {
    if (owner === current) loading.value = false;
  }
}, { immediate: true });
onBeforeUnmount(() => owner?.abort());
</script>

<template>
  <nexus-modal :open.prop="visible" :title="t('新建脚本实例')" @nexus-close="emit('close')">
    <div class="script-types" :aria-busy="loading">
      <template v-if="ready || !store.can('extensions.read')">
      <button type="button" class="type-choice" @click="emit('choose')">
        <span class="type-icon"><img :src="scriptIcon()" alt="" /></span>
        <span>
          <strong>{{ t('通用脚本') }}</strong>
          <small>{{ t('手动配置主程序、启动参数、配置与日志路径') }}</small>
        </span>
      </button>
      <template v-for="(choice, index) in choices" :key="choice.extensionId">
        <hr v-if="!choice.driver && (index === 0 || choices[index - 1]?.driver)" class="type-divider" />
        <button
          type="button"
          class="type-choice"
          :disabled="!store.can('automation.declare')"
          @click="emit('choose', choice)"
        >
          <span class="type-icon"><img :src="scriptIcon(choice.extensionId)" alt="" /></span>
          <span>
            <strong>{{ choice.driver ? choice.label : choice.label + ' ' + t('专项') }}</strong>
            <small>{{ t(choice.driver ? '由驱动插件编译并执行项目' : '由专用插件自动适配配置') }}</small>
          </span>
        </button>
      </template>
      </template>
      <p v-if="!ready && loading" role="status">{{ t('加载中…') }}</p>
      <p v-if="choices.length && !store.can('automation.declare')">{{ t('请在 Host 本机创建专项脚本实例。') }}</p>
      <p v-if="error" role="alert">{{ t(error) }}</p>
    </div>
    <div slot="footer">
      <button type="button" @click="emit('close')">{{ t('取消') }}</button>
    </div>
  </nexus-modal>
</template>

<style scoped>
.script-types {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}
.script-types > p { grid-column: 1 / -1; }
.type-divider { grid-column: 1 / -1; width: 100%; margin: 0; border: 0; border-top: 1px solid var(--border); }
.type-choice {
  display: flex;
  align-items: center;
  gap: 14px;
  width: 100%;
  padding: 14px 16px;
  text-align: left;
  background: var(--content-control);
}
.type-choice:hover {
  border-color: var(--accent);
  background: var(--accent-soft);
}
.type-choice > span:last-child {
  display: grid;
  min-width: 0;
  gap: 8px;
  line-height: 1.5;
}
.type-choice strong {
  font-size: 14px;
}
.type-choice small {
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
}
.type-icon {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  flex: none;
  border-radius: 11px;
  font-size: 20px;
  color: white;

}
.type-icon img { width: 36px; height: 36px; object-fit: contain; }
[slot='footer'] > button {
  min-width: 80px;
  color: var(--muted);
}
p {
  margin: 0;
  color: var(--bad);
}
@media (max-width: 600px) {
  .type-icon img { width: 36px; height: 36px; object-fit: contain; }
.script-types {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
