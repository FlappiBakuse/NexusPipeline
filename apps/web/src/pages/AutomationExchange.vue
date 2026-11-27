<script setup lang="ts">
import { fieldHelp as vHelp } from '../components/field-help';
import { createIdentity } from '../control/identity';
import { computed, ref, watch } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import { arrayValue, ControlFailure, integerValue, objectValue, stringValue } from '../control/client';
import type { AutomationBundle, ControlAutomationImport } from '../control/wire.generated';
import { t } from '../app/locale';
import { desktop } from '../app/desktop';
import { providePathPicker } from './editor-path-picker';
import './automation-messages';

type Mapping = { id: string; name: string; path: string };
type Preview = {
  operationId: string;
  previewDigest: string;
  expectedRevision: number;
  canApply: boolean;
  paths: Record<string, unknown>[];
  problems: Record<string, unknown>[];
};
const props = defineProps<{ store: AutomationStore; file?: File; visible: boolean }>();
const emit = defineEmits<{ applied: [] }>();
const bundle = ref<AutomationBundle | null>(null);
const fileName = ref('');
const roots = ref<Mapping[]>([]);
const attachments = ref<Mapping[]>([]);
const preview = ref<Preview | null>(null);
const operationId = ref('');
const pendingApply = ref('');
const busy = ref(false);
const error = ref('');
const notice = ref('');
const choosePath = providePathPicker(() => props.visible, () => props.visible, reason => props.store.report(reason));
const ready = computed(
  () =>
    bundle.value &&
    roots.value.every((root) => root.path.trim()) &&
    attachments.value.every((item) => item.path.trim())
);

function invalidate(): void {
  if (pendingApply.value) return;
  preview.value = null;
  operationId.value = '';
  notice.value = '';
}
function setMapping(item: Mapping, path: string): void {
  if (pendingApply.value || busy.value) return;
  item.path = path;
  invalidate();
}
async function browse(item: Mapping, kind: 'file' | 'folder'): Promise<void> {
  if (pendingApply.value || busy.value) return;
  const path = await choosePath(kind);
  if (path && (kind === 'folder' ? roots : attachments).value.includes(item)) setMapping(item, path);
}
async function chooseFile(file: File | undefined): Promise<void> {
  if (pendingApply.value || busy.value) return;
  if (!file) return;
  error.value = '';
  invalidate();
  bundle.value = null;
  roots.value = [];
  attachments.value = [];
  busy.value = true;
  try {
    if (file.size > 16 * 1024 * 1024) throw new Error('导入文件超过 16 MiB。');
    const data = objectValue(JSON.parse(await file.text()));
    if (data.format !== 'nexus.automation.bundle' || data.version !== 1)
      throw new TypeError('请选择新版自动化 bundle JSON。');
    if (
      data.exportKind !== 'scriptTemplate' ||
      !Array.isArray(data.scripts) ||
      data.scripts.length !== 1 ||
      data.scripts[0]?.kind !== 'generic'
    )
      throw new TypeError('请选择新版通用脚本模板。');
    const imported = data as unknown as AutomationBundle;
    const saved = props.store.state.automation?.roots || [];
    const nextRoots = arrayValue(data.roots, (value) => {
      const root = objectValue(value),
        id = stringValue(root.id),
        name = stringValue(root.name);
      return { id, name, path: saved.find((item) => item.id === id)?.path || '' };
    });
    const nextAttachments = arrayValue(data.attachments, (value) => {
      const item = objectValue(value);
      return { id: stringValue(item.id), name: stringValue(item.path), path: '' };
    });
    invalidate();
    roots.value = nextRoots;
    attachments.value = nextAttachments;
    bundle.value = imported;
    fileName.value = file.name;
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : '导入文件无法解析。';
  } finally {
    busy.value = false;
  }
}
watch(
  () => props.file,
  (file) => {
    if (file) void chooseFile(file);
  },
  { immediate: true }
);
function command(action: 'preview' | 'apply'): ControlAutomationImport {
  return {
    format: 'nexus.control.automation-import',
    version: 1,
    action,
    operationId: operationId.value,
    expectedRevision: preview.value?.expectedRevision ?? props.store.state.automation?.catalogRevision ?? 0,
    previewDigest: preview.value?.previewDigest ?? '0'.repeat(64),
    bundle: bundle.value!,
    roots: roots.value.map(({ id, path }) => ({ id, path: path.trim() })),
    attachments: attachments.value.map(({ id, path }) => ({ id, path: path.trim() }))
  };
}
async function inspect(): Promise<void> {
  if (!ready.value || busy.value || pendingApply.value || !props.store.can('automation.exchange')) return;
  busy.value = true;
  error.value = '';
  notice.value = '';
  operationId.value = createIdentity();
  preview.value = null;
  try {
    const row = await props.store.client.write(
      '/control/v1/automation/import/preview',
      command('preview'),
      objectValue,
      operationId.value
    );
    preview.value = {
      operationId: stringValue(row.operationId),
      previewDigest: stringValue(row.previewDigest),
      expectedRevision: integerValue(row.expectedRevision),
      canApply: row.canApply === true,
      paths: arrayValue(row.paths, objectValue),
      problems: arrayValue(row.problems, objectValue)
    };
    notice.value = preview.value.canApply
      ? '预览可应用；请核对路径与附件后执行导入。'
      : '预览发现阻止应用的问题。';
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : '导入预览失败。';
  } finally {
    busy.value = false;
  }
}
async function apply(): Promise<void> {
  if (
    !preview.value?.canApply ||
    !bundle.value ||
    busy.value ||
    pendingApply.value ||
    !props.store.can('automation.exchange')
  )
    return;
  busy.value = true;
  error.value = '';
  try {
    await props.store.client.write(
      '/control/v1/automation/import/apply',
      command('apply'),
      objectValue,
      operationId.value
    );
    pendingApply.value = operationId.value;
    notice.value = '导入已受理。';
    await finishApply();
  } catch (reason) {
    if (reason instanceof ControlFailure && reason.uncertain) pendingApply.value = operationId.value;
    error.value = pendingApply.value
      ? '导入结果待确认；请查询状态，勿重复应用。'
      : reason instanceof Error
        ? reason.message
        : '导入失败；草稿与预览已保留。';
  } finally {
    busy.value = false;
  }
}
async function finishApply(): Promise<void> {
  await props.store.refresh();
  emit('applied');
  preview.value = null;
  pendingApply.value = '';
  error.value = '';
}
async function checkApply(): Promise<void> {
  if (!pendingApply.value || busy.value) return;
  busy.value = true;
  error.value = '';
  try {
    const state = await props.store.client.read('/control/v1/operations/' + pendingApply.value, objectValue);
    if (state.operationId !== pendingApply.value || state.kind !== 'automation.import')
      throw new Error('操作类型不匹配，需人工核对。');
    if (state.status === 'completed') {
      await finishApply();
      notice.value = '导入已完成，目录已刷新。';
    } else notice.value = '导入仍在处理，请稍后再查询。';
  } catch {
    error.value = '尚无法确认导入结果；草稿与预览已保留，原命令仍被锁定。';
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <section class="exchange import-only">
    <div class="file-action">
      <nexus-file-picker
        v-help="t('导入使用新版 bundle JSON。根目录和附件路径指向运行 Host 的本机文件系统。')"
        :label="t('选择 bundle JSON')"
        :aria-label="t('选择 bundle JSON')"
        accept=".json,application/json"
        :disabled.prop="!!pendingApply || busy"
        @nexus-change="chooseFile($event.detail.value[0])"
      >
        <span slot="label">{{ t('选择 bundle JSON') }}</span>
      </nexus-file-picker>
    </div>
    <template v-if="bundle">
      <p>
        {{ t('已选择：') }}{{ fileName }} ·
        {{ t(bundle.exportKind === 'scriptTemplate' ? '脚本模板' : '完整定义') }}
      </p>
      <fieldset data-surface="raised">
        <legend>{{ t('导入根目录') }}</legend>
        <label v-for="root in roots" :key="root.id">
          {{ root.name }}
          <nexus-path-picker
            :value.prop="root.path"
            :disabled.prop="!!pendingApply || busy"
            :aria-label="root.name + t('的 Host 路径')"
            :placeholder="t('例如 D:\\Scripts')"
            :kinds.prop="['folder']"
            :browse-allowed.prop.camel="!!desktop"
            @nexus-change="setMapping(root, $event.detail.value)"
            @nexus-browse="browse(root, 'folder')"
          />
        </label>
      </fieldset>
      <fieldset v-if="attachments.length" data-surface="raised">
        <legend>{{ t('附件原件路径') }}</legend>
        <label v-for="item in attachments" :key="item.id">
          {{ item.name }}
          <nexus-path-picker
            :value.prop="item.path"
            :disabled.prop="!!pendingApply || busy"
            :aria-label="item.name + t('的 Host 原件路径')"
            :kinds.prop="['file']"
            :browse-allowed.prop.camel="!!desktop"
            @nexus-change="setMapping(item, $event.detail.value)"
            @nexus-browse="browse(item, 'file')"
          />
        </label>
      </fieldset>
      <div class="row">
        <button
          :disabled="!ready || busy || !!pendingApply || !store.can('automation.exchange')"
          @click="inspect"
        >
          {{ t('预览导入') }}
        </button>
        <button
          :disabled="!preview?.canApply || busy || !!pendingApply || !store.can('automation.exchange')"
          @click="apply"
        >
          {{ t('应用已核对预览') }}
        </button>
        <button v-if="pendingApply" :disabled="busy || !store.can('operations.read')" @click="checkApply">
          {{ t('查询操作状态并刷新') }}
        </button>
      </div>
      <div v-if="preview" class="preview" data-surface="raised">
        <h3>{{ t('预览') }} · {{ t(preview.canApply ? '可应用' : '不可应用') }}</h3>
        <p>
          {{ t('目录版本') }} {{ preview.expectedRevision }}; {{ preview.paths.length }} {{ t('条路径') }};
          {{ preview.problems.length }} {{ t('个问题。') }}
        </p>
        <ul>
          <li v-for="(problem, index) in preview.problems" :key="index">
            {{ problem.code }} · {{ problem.path }}
          </li>
        </ul>
      </div>
    </template>
    <nexus-toast v-if="error" :tone="pendingApply ? 'warning' : 'danger'" :message.prop="t(error)" />
    <nexus-toast v-else-if="notice" tone="info" :message.prop="t(notice)" />
  </section>
</template>

<style scoped>
.exchange {
  display: grid;
  gap: 14px;
  padding-top: 18px;
  border-top: 1px solid var(--border);
}
h2,
h3,
p {
  margin: 0;
}
.import-only {
  padding-top: 0;
  border-top: 0;
}
fieldset {
  display: grid;
  gap: 8px;
  border: 1px solid var(--border);
  border-radius: var(--nx-radius-md);
  box-shadow: var(--nx-surface-shadow);
}
label {
  display: grid;
  gap: 5px;
}
input,
select {
  min-height: 40px;
  width: 100%;
  padding: 7px;
  border: 1px solid var(--border);
  border-radius: var(--nx-radius-sm);
  color: inherit;
  background: var(--content-control);
  box-shadow: var(--nx-surface-shadow);
}
.export {
  display: grid;
  gap: 8px;
  padding-top: 16px;
  border-top: 1px solid var(--border);
}
.export label {
  max-width: 400px;
}
.preview {
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: var(--nx-radius-md);
  box-shadow: var(--nx-surface-shadow);
}
.error {
  color: var(--bad);
}
ul {
  margin: 0;
  padding-left: 22px;
  overflow-wrap: anywhere;
}
</style>
