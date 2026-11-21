<script setup lang="ts">
import { fieldHelp as vHelp } from '../components/field-help';
import { computed, onBeforeUnmount, ref } from 'vue';
import type { AutomationBundle } from '../control/wire.generated';
import type { AutomationStore } from '../app/store-ports';
import { desktop } from '../app/desktop';
import { usePathPicker } from './editor-path-picker';
import { t, registerMessages } from '../app/locale';
import { templateKey } from './script-template';
import { scriptPaths, scriptPathKey, scriptRootValid, type ScriptPaths } from './script-paths';
import ScriptLimits from './ScriptLimits.vue';
import GameFields from './GameFields.vue';
import ScriptSourceFields from './ScriptSourceFields.vue';
import { useScriptRoot } from './script-root';
registerMessages({
  '选择脚本在 Host 上的安装目录，系统会自动读取配置。': 'Choose the script installation directory on the Host to read its configuration.'
});
type Script = AutomationBundle['scripts'][number];
const props = defineProps<{ modelValue: Script; store: AutomationStore }>();
const emit = defineEmits<{ 'update:modelValue': [value: Script] }>();
const paths = computed(() => scriptPaths(props.store, props.modelValue));
const choosePath = usePathPicker();
const rootInput = ref<HTMLElement>();
const { rootReady, rootMessage } = useScriptRoot(props.store, () => props.modelValue.id,
  () => props.modelValue.kind === 'generic' ? paths.value.root : '', rootInput);
const uploadInput = ref<HTMLInputElement>();
let fileRevision = 0;
onBeforeUnmount(() => fileRevision++);
function update<K extends keyof Script>(key: K, value: Script[K]): void {
  emit('update:modelValue', { ...props.modelValue, [key]: value });
}
function path(key: Exclude<keyof ScriptPaths, 'judgeLanguage' | 'emulatorRoot'>, value: string): void {
  if (key !== 'root' && ['executable', 'configuration', 'logs', 'arguments'].includes(key) && !rootReady.value) return;
  paths.value[key] = value;
  if (key === 'root' && scriptRootValid(value) && props.store.drafts.has(templateKey(props.modelValue.id))) {
    const root = value.trim().replace(/[\\/]$/, '');
    const absolute = (path: string) => !path.trim() || /^(?:[a-z]:[\\/]|[\\/]{2})/i.test(path)
      ? path : root + '\\' + path.replace(/\//g, '\\');
    for (const field of ['executable', 'configuration', 'logs', 'gamePath', 'androidTool'] as const)
      paths.value[field] = paths.value[field].split(/\r?\n/).map(absolute).join('\n');
  }
  props.store.dirty.add(scriptPathKey(props.modelValue.id));
}
async function browse(key: Exclude<keyof ScriptPaths, 'judgeLanguage' | 'emulatorRoot'>, kind: 'file' | 'folder'): Promise<void> {
  if (key !== 'root' && !rootReady.value) return;
  const root = paths.value.root;
  const selected = await choosePath(kind);
  if (selected && root === paths.value.root) path(key, selected);
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
      <label v-help="t('实例名称用于列表与运行记录，可与脚本文件名不同。')">
        <span class="field-help-caption"><span>{{ t('脚本名称') }} </span><em>*</em></span>
        <nexus-text-input
        data-draft-field="ScriptFields/modelValue.name"
          :aria-label="t('脚本名称')"
          aria-required="true"
          :value.prop="modelValue.name"
          :max-length.prop.camel="120"
          @nexus-change="update('name', $event.detail.value)"
        />
      </label>
      <label v-help="rootMessage || t(modelValue.kind === 'generic'
        ? '填写 Host 上实际存在的完整脚本目录，例如 D:\\Scripts\\Example；核验通过后才能编辑下方四项。'
        : '选择脚本在 Host 上的安装目录，系统会自动读取配置。')">
        <span class="field-help-caption"><span>{{ t('脚本根目录') }} </span><em>*</em></span>
        <nexus-path-picker
          ref="rootInput"
          :placeholder="t('脚本根目录')"
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
        <label v-help="t('先填写脚本根目录，再选择主程序。')">
          <span class="field-help-caption"><span>{{ t('脚本主程序路径') }} </span><em>*</em></span>
          <nexus-path-picker
            :placeholder="t('脚本主程序文件')"
          :aria-label="t('脚本主程序路径')"
            aria-required="true"
            :value.prop="paths.executable"
            :disabled.prop="!rootReady"
            :kinds.prop="['file']"
            :browse-allowed.prop.camel="!!desktop"
            @nexus-change="path('executable', $event.detail.value)"
            @nexus-browse="browse('executable', $event.detail.kind)"
          />
        </label>
        <label v-help="t('启动时传给脚本主程序的命令行参数；包含空格的参数使用双引号，留空表示不追加参数。')">
          <span class="field-help-caption"><span>{{ t('脚本自启动参数') }}</span></span>
          <nexus-text-input
        data-draft-field="ScriptFields/paths.arguments"
            :aria-label="t('脚本启动参数')"
            :placeholder="t('可选启动参数')"
            :value.prop="paths.arguments"
            :disabled.prop="!rootReady"
            @nexus-change="path('arguments', $event.detail.value)"
          />
        </label>
        <label v-help="t('先填写脚本根目录，再选择配置文件或文件夹。')">
          <span class="field-help-caption"><span>{{ t('配置文件路径/文件夹') }} </span><em>*</em></span>
          <nexus-path-picker
            :placeholder="t('配置文件或文件夹路径')"
          :aria-label="t('配置文件路径/文件夹')"
            aria-required="true"
            :value.prop="paths.configuration"
            :disabled.prop="!rootReady"
            :kinds.prop="['file', 'folder']"
            :browse-allowed.prop.camel="!!desktop"
            @nexus-change="path('configuration', $event.detail.value)"
            @nexus-browse="browse('configuration', $event.detail.kind)"
          />
        </label>
        <label v-help="t('支持日志文件、目录或多行路径；日期使用 {yyyy-MM-dd} 等格式，文件名可用 * 通配。目录和匹配结果读取最新文件，不支持 ? 或目录通配符。')">
          <span class="field-help-caption"><span>{{ t('日志路径（支持日期占位符与通配符）') }} </span><em>*</em></span>
          <nexus-path-picker
            :placeholder="t('日志文件路径')"
          :aria-label="t('日志路径')"
            aria-required="true"
            :value.prop="paths.logs"
            :disabled.prop="!rootReady"
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
    <ScriptLimits :model-value="modelValue.limits" @update:model-value="update('limits', $event)" />
    <section v-if="modelValue.kind === 'generic'" class="script-section judgement">
      <h3>{{ t('自定义完成标志') }}</h3>
      <template v-if="modelValue.judgement.language === 'keywords'">
        <label v-help="t('每行一组条件，逗号分隔的词需全部命中，多行满足任一组即可；留空不使用成功关键字。')">
          <span class="field-help-caption"><span>{{ t('成功关键字') }}</span></span>
          <nexus-text-area
        data-draft-field="ScriptFields/modelValue.judgement.successKeywords.join('\n')"
            class="keywords"
            :placeholder="t('逗号表示 AND，多行表示 OR')"
          :aria-label="t('成功关键字')"
            :rows.prop="4"
            :value.prop="modelValue.judgement.successKeywords.join('\n')"
            @nexus-change="keywords('successKeywords', $event.detail.value)"
          />
        </label>
        <label v-help="t('语法与成功关键字相同；命中后结束当前尝试，是否重试由重试策略决定。')">
          <span class="field-help-caption"><span>{{ t('失败关键字') }}</span></span>
          <nexus-text-area
        data-draft-field="ScriptFields/modelValue.judgement.failureKeywords.join('\n')"
            class="keywords"
            :placeholder="t('命中即判定失败')"
          :aria-label="t('失败关键字')"
            :rows.prop="4"
            :value.prop="modelValue.judgement.failureKeywords.join('\n')"
            @nexus-change="keywords('failureKeywords', $event.detail.value)"
          />
        </label>
      </template>
      <template v-else>
        <label v-help="t('JavaScript 使用内置引擎，Python 使用系统解释器；两种语言使用各自的判定函数入口。')">
          <span class="field-help-caption"><span>{{ t('判断脚本语言') }}</span></span>
          <FormSelect
        data-draft-field="ScriptFields/modelValue.judgement.language"
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
        <label v-help="t('JavaScript 定义 nexusJudge(input)，Python 定义 nexus_judge(input)，返回 outcome 和 reason。outcome 可为 unverified、succeeded、partial 或 failed。')">
          <span class="field-help-caption">
            <span>{{ t('判断脚本 代码') }}</span>
            <em>*</em>
          </span>
          <nexus-text-area
        data-draft-field="ScriptFields/modelValue.judgement.script || ''"
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
          <input
        data-draft-field="ScriptFields/field-6720" ref="uploadInput" type="file" accept=".js,.py" hidden @change="upload" />
        </template>
        <nexus-switch-setting
        data-draft-field="ScriptFields/modelValue.judgement.language !== 'keywords'"
          class="judge-switch"
          :class="{ enabled: modelValue.judgement.language !== 'keywords' }"
          :title="t('使用判断脚本')"
          :description="t('启用后使用所选语言的判定函数；关闭后使用成功与失败关键字。')"
          :value.prop="modelValue.judgement.language !== 'keywords'"
          @nexus-change="toggleJudge($event.detail.value)"
        />
      </div>
    </section>
  </div>
</template>

<style scoped src="./script-fields.css"></style>
