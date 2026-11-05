<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { objectValue } from '../control/client';
import { registerMessages, t } from '../app/locale';

const props = defineProps<{
  store: AppStore;
  bindingId: string;
  catalogRevision: number;
  visible: boolean;
}>();
const fields = [
  ['isEnabled', '是否启用'],
  ['runDays', '运行天数'],
  ['maximumSuccessfulRunsPerDay', '每日成功上限'],
  ['notifyEnabled', '是否通知'],
  ['notificationTaskIds', '通知任务'],
  ['preHook', '前置动作'],
  ['postHook', '后置动作'],
  ['preOnceAfterSuccess', '前置成功后只运行一次'],
  ['postFinalOnly', '后置仅最终运行']
] as const;
type Field = (typeof fields)[number][0];
type View = {
  catalogRevision: number;
  bindingId: string;
  userId: string;
  raw: Record<string, unknown>;
  effective: Record<string, unknown>;
  sources: Record<Field, 'user' | 'binding'>;
};
const view = ref<View>();
const loading = ref(false);
const error = ref('');
let owner: AbortController | undefined;
registerMessages({
  最终有效值: 'Effective values',
  来源: 'Source',
  绑定本身: 'Binding',
  账号全局覆盖: 'Account override',
  重新读取有效值: 'Reload effective values',
  有效值尚未读取: 'Effective values have not been loaded',
  '有效值正在读取…': 'Loading effective values…',
  '有效值已变化，请刷新绑定目录。': 'Values changed; refresh the binding catalog.',
  '有效值来源不完整。': 'Effective value sources are incomplete.',
  是否启用: 'Enabled',
  运行天数: 'Run days',
  每日成功上限: 'Daily success limit',
  是否通知: 'Notifications enabled',
  通知任务: 'Notification tasks',
  前置动作: 'Pre-action',
  后置动作: 'Post-action',
  前置成功后只运行一次: 'Pre-action once after success',
  后置仅最终运行: 'Post-action on final run only',
  目录修订: 'Catalog revision',
  无: 'None',
  是: 'Yes',
  否: 'No'
});
function display(value: unknown): string {
  if (value == null) return t('无');
  if (typeof value === 'boolean') return t(value ? '是' : '否');
  if (Array.isArray(value)) return value.length ? value.map((item) => String(item)).join(', ') : t('无');
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}
async function load(): Promise<void> {
  owner?.abort();
  view.value = undefined;
  error.value = '';
  if (!props.visible || !props.bindingId || !props.store.can('automation.read')) return;
  const current = new AbortController();
  owner = current;
  loading.value = true;
  try {
    const row = await props.store.client.read(
      '/control/v1/automation/bindings/' + encodeURIComponent(props.bindingId) + '/effective',
      objectValue,
      current.signal
    );
    current.signal.throwIfAborted();
    if (row.bindingId !== props.bindingId || row.catalogRevision !== props.catalogRevision) {
      error.value = '有效值已变化，请刷新绑定目录。';
      return;
    }
    const effective = objectValue(row.effective),
      sources = objectValue(row.sources);
    for (const [field] of fields) {
      if (!Object.hasOwn(effective, field) || !['user', 'binding'].includes(String(sources[field])))
        throw Error('有效值来源不完整。');
    }
    view.value = {
      catalogRevision: Number(row.catalogRevision),
      bindingId: String(row.bindingId),
      userId: String(row.userId),
      raw: objectValue(row.raw),
      effective,
      sources: sources as View['sources']
    };
  } catch (reason) {
    if (!current.signal.aborted) error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (owner === current) loading.value = false;
  }
}
watch(
  () => [props.bindingId, props.catalogRevision, props.visible] as const,
  () => {
    void load();
  },
  { immediate: true }
);
onBeforeUnmount(() => owner?.abort());
</script>

<template>
  <nexus-section-card :title="t('最终有效值')">
    <div class="row">
      <button type="button" :disabled="loading || !visible || !store.can('automation.read')" @click="load">
        {{ t('重新读取有效值') }}
      </button>
      <small v-if="view">{{ t('目录修订') }} {{ view.catalogRevision }}</small>
    </div>
    <p v-if="error" role="alert" class="effective-error">{{ t(error) }}</p>
    <p v-else-if="loading" role="status">{{ t('有效值正在读取…') }}</p>
    <p v-else-if="!view" role="status">{{ t('有效值尚未读取') }}</p>
    <dl v-else class="effective-list">
      <div v-for="[key, label] in fields" :key="key">
        <dt>{{ t(label) }}</dt>
        <dd>
          <span>{{ display(view.effective[key]) }}</span>
          <small>{{ t('来源') }}：{{ t(view.sources[key] === 'user' ? '账号全局覆盖' : '绑定本身') }}</small>
        </dd>
      </div>
    </dl>
  </nexus-section-card>
</template>

<style scoped>
.effective-list {
  display: grid;
  gap: 7px;
  margin: 12px 0 0;
}
.effective-list > div {
  display: grid;
  grid-template-columns: minmax(130px, 1fr) minmax(0, 2fr);
  gap: 10px;
  border-bottom: 1px solid var(--border);
  padding-bottom: 7px;
}
.effective-list dt {
  color: var(--muted);
}
.effective-list dd {
  display: grid;
  gap: 2px;
  min-width: 0;
  margin: 0;
  overflow-wrap: anywhere;
}
.effective-list small {
  color: var(--muted);
}
.effective-error {
  color: var(--bad);
  margin: 8px 0 0;
}
@media (max-width: 600px) {
  .effective-list > div {
    grid-template-columns: 1fr;
  }
}
</style>
