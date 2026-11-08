<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue';
import type { AutomationBundle } from '../control/wire.generated';
import type { AutomationStore } from '../app/store-ports';
import { desktop } from '../app/desktop';
import { t } from '../app/locale';
import { templateKey } from './script-template';
import { scriptPaths, scriptPathKey, type ScriptPaths } from './script-paths';
import GameFields from './GameFields.vue';
import ScriptSourceFields from './ScriptSourceFields.vue';
type Script = AutomationBundle['scripts'][number];
const props = defineProps<{ modelValue: Script; store: AutomationStore }>();
const emit = defineEmits<{ 'update:modelValue': [value: Script] }>();
const paths = computed(() => scriptPaths(props.store, props.modelValue));
const uploadInput = ref<HTMLInputElement>();
let fileRevision = 0;
onBeforeUnmount(() => fileRevision++);
function update<K extends keyof Script>(key: K, value: Script[K]): void {
  emit('update:modelValue', { ...props.modelValue, [key]: value });
}
function path(key: Exclude<keyof ScriptPaths, 'judgeLanguage' | 'emulatorRoot'>, value: string): void {
  paths.value[key] = value;
  if (key === 'root' && props.store.drafts.has(templateKey(props.modelValue.id))) {
    const root = value.trim().replace(/[\\/]$/, '');
    const absolute = (path: string) => !path.trim() || /^(?:[a-z]:[\\/]|[\\/]{2})/i.test(path)
      ? path : root + '\\' + path.replace(/\//g, '\\');
    for (const field of ['executable', 'configuration', 'logs', 'gamePath', 'androidTool'] as const)
      paths.value[field] = paths.value[field].split(/\r?\n/).map(absolute).join('\n');
  }
  props.store.dirty.add(scriptPathKey(props.modelValue.id));
}
async function browse(key: Exclude<keyof ScriptPaths, 'judgeLanguage' | 'emulatorRoot'>, kind: string): Promise<void> {
  try {
    const result = kind === 'folder' ? await desktop?.chooseFolder() : await desktop?.chooseFile();
    if (result && !result.canceled && result.paths[0]) path(key, result.paths[0]);
  } catch (reason) {
    props.store.report(reason);
  }
}
function limit(key: keyof Script['limits'], value: number): void {
  update('limits', {
    ...props.modelValue.limits,
    [key]: key === 'maximumAttempts' ? value : Math.round(value * 60)
  });
}
function keywords(key: 'successKeywords' | 'failureKeywords', value: string): void {
  update('judgement', {
    ...props.modelValue.judgement,
    [key]: value.split(/\r?\n/).filter((item) => item.length)
  });
}
function toggleJudge(enabled: boolean): void {
  const language = props.modelValue.judgement.language;
  if (language !== 'keywords') paths.value.judgeLanguage = language;
  update('judgement', {
    ...props.modelValue.judgement,
    language: enabled ? paths.value.judgeLanguage || 'javascript' : 'keywords'
  });
}
async function upload(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement,
    file = input.files?.[0];
  input.value = '';
  if (!file) return;
  if (file.size > 65536) {
    props.store.report(new Error('判定脚本超过大小限制。'));
    return;
  }
  const ticket = ++fileRevision,
    scriptId = props.modelValue.id;
  try {
    const text = await file.text();
    if (
      ticket !== fileRevision ||
      props.modelValue.id !== scriptId ||
      props.modelValue.judgement.language === 'keywords'
    )
      return;
    update('judgement', {
      ...props.modelValue.judgement,
      language: /\.py$/i.test(file.name) ? 'python' : 'javascript',
      script: text
    });
  } catch (reason) {
    props.store.report(reason);
  }
}
</script>

<template>
  <div class="script-fields">
    <div class="columns">
      <label>
        {{ t('脚本名称') }}
        <em>*</em>
        <nexus-text-input
          :aria-label="t('脚本名称')"
          aria-required="true"
          :value.prop="modelValue.name"
          :max-length.prop.camel="120"
          @nexus-change="update('name', $event.detail.value)"
        />
      </label>
      <label>
        {{ t('脚本根目录') }}
        <em>*</em>
        <nexus-path-picker
          :aria-label="t('脚本根目录')"
          aria-required="true"
          :value.prop="paths.root"
          :kinds.prop="['folder']"
          :browse-allowed.prop.camel="!!desktop"
          @nexus-change="path('root', $event.detail.value)"
          @nexus-browse="browse('root', $event.detail.kind)"
        />
      </label>
      <template v-if="modelValue.kind === 'generic'">
        <label>
          {{ t('脚本主程序路径') }}
          <em>*</em>
          <nexus-path-picker
            :aria-label="t('脚本主程序路径')"
            aria-required="true"
            :value.prop="paths.executable"
            :disabled.prop="!paths.root.trim()"
            :kinds.prop="['file']"
            :browse-allowed.prop.camel="!!desktop"
            @nexus-change="path('executable', $event.detail.value)"
            @nexus-browse="browse('executable', $event.detail.kind)"
          />
        </label>
        <label>
          {{ t('脚本自启动参数') }}
          <nexus-text-input
            :aria-label="t('脚本启动参数')"
            :placeholder="t('可选启动参数')"
            :value.prop="paths.arguments"
            :disabled.prop="!paths.root.trim()"
            @nexus-change="path('arguments', $event.detail.value)"
          />
        </label>
        <label>
          {{ t('配置文件路径/文件夹') }}
          <em>*</em>
          <nexus-path-picker
            :aria-label="t('配置文件路径/文件夹')"
            aria-required="true"
            :value.prop="paths.configuration"
            :disabled.prop="!paths.root.trim()"
            :kinds.prop="['file', 'folder']"
            :browse-allowed.prop.camel="!!desktop"
            @nexus-change="path('configuration', $event.detail.value)"
            @nexus-browse="browse('configuration', $event.detail.kind)"
          />
        </label>
        <label>
          {{ t('日志路径（支持日期占位符与通配符）') }}
          <em>*</em>
          <nexus-path-picker
            :aria-label="t('日志路径')"
            aria-required="true"
            :value.prop="paths.logs"
            :disabled.prop="!paths.root.trim()"
            :kinds.prop="['file', 'folder']"
            :browse-allowed.prop.camel="!!desktop"
            @nexus-change="path('logs', $event.detail.value)"
            @nexus-browse="browse('logs', $event.detail.kind)"
          />
        </label>
      </template>
    </div>
    <ScriptSourceFields v-if="modelValue.kind === 'specialized'" :store="store" :model-value="modelValue"
      @update:model-value="emit('update:modelValue', $event)" />
    <GameFields
      :model-value="modelValue"
      :store="store"
      @update:model-value="emit('update:modelValue', $event)"
    />
    <section class="script-section">
      <h3>{{ t('运行设置') }}</h3>
      <div class="columns three">
        <label>
          {{ t('最大尝试次数（含首次）') }}
          <em>*</em>
          <nexus-number-input
            :aria-label="t('最大尝试次数（含首次）')"
            aria-required="true"
            :min.prop="1"
            :max.prop="99"
            :value.prop="modelValue.limits.maximumAttempts"
            @nexus-change="limit('maximumAttempts', $event.detail.value)"
          />
        </label>
        <label>
          {{ t('日志无更新上限（分钟）') }}
          <em>*</em>
          <nexus-number-input
            :aria-label="t('日志无更新上限（分钟）')"
            aria-required="true"
            :min.prop="0"
            :max.prop="1440"
            :value.prop="modelValue.limits.stallTimeoutSeconds / 60"
            @nexus-change="limit('stallTimeoutSeconds', $event.detail.value)"
          />
        </label>
        <label>
          {{ t('运行总时间上限（分钟）') }}
          <em>*</em>
          <nexus-number-input
            :aria-label="t('运行总时间上限（分钟）')"
            aria-required="true"
            :min.prop="0"
            :max.prop="10080"
            :value.prop="modelValue.limits.totalTimeoutSeconds / 60"
            @nexus-change="limit('totalTimeoutSeconds', $event.detail.value)"
          />
        </label>
      </div>
    </section>
    <section v-if="modelValue.kind === 'generic'" class="script-section judgement">
      <h3>{{ t('自定义完成标志') }}</h3>
      <template v-if="modelValue.judgement.language === 'keywords'">
        <label>
          {{ t('成功关键字') }}
          <nexus-text-area
            class="keywords"
            :aria-label="t('成功关键字')"
            :rows.prop="4"
            :value.prop="modelValue.judgement.successKeywords.join('\n')"
            @nexus-change="keywords('successKeywords', $event.detail.value)"
          />
        </label>
        <label>
          {{ t('失败关键字') }}
          <nexus-text-area
            class="keywords"
            :aria-label="t('失败关键字')"
            :rows.prop="4"
            :value.prop="modelValue.judgement.failureKeywords.join('\n')"
            @nexus-change="keywords('failureKeywords', $event.detail.value)"
          />
        </label>
      </template>
      <template v-else>
        <label>
          {{ t('判断脚本语言') }}
          <FormSelect
            :aria-label="t('判断脚本语言')"
            :value="modelValue.judgement.language"
            @change="
              update('judgement', {
                ...modelValue.judgement,
                language: ($event.target as HTMLSelectElement).value as 'javascript' | 'python'
              })
            "
          >
            <option value="javascript">{{ t('JavaScript（内置引擎）') }}</option>
            <option value="python">{{ t('Python（系统解释器）') }}</option>
          </FormSelect>
        </label>
        <label>
          <span>
            {{ t('判断脚本 代码') }}
            <em>*</em>
          </span>
          <nexus-text-area
            class="judge-code"
            :aria-label="t('判断脚本')"
            aria-required="true"
            :rows.prop="4"
            :max-length.prop.camel="65536"
            :placeholder="t('输出 JSON 结果')"
            :value.prop="modelValue.judgement.script || ''"
            @nexus-change="update('judgement', { ...modelValue.judgement, script: $event.detail.value })"
          />
        </label>
      </template>
      <div class="judge-actions">
        <template v-if="modelValue.judgement.language !== 'keywords'">
          <button type="button" class="upload-script" @click="uploadInput?.click()">
            {{ t('上传脚本文件') }}
          </button>
          <input ref="uploadInput" type="file" accept=".js,.py" hidden @change="upload" />
        </template>
        <nexus-switch-setting
          class="judge-switch"
          :class="{ enabled: modelValue.judgement.language !== 'keywords' }"
          :title="t('使用判断脚本')"
          :description="t('脚本优先')"
          :value.prop="modelValue.judgement.language !== 'keywords'"
          @nexus-change="toggleJudge($event.detail.value)"
        />
      </div>
    </section>
  </div>
</template>

<style scoped src="./script-fields.css"></style>
