<script setup lang="ts">
import { computed, watch } from 'vue';
import { useAdapterAvailability } from './adapter-availability';
import type { AutomationBundle } from '../control/wire.generated';
import type { AutomationStore } from '../app/store-ports';
import { desktop } from '../app/desktop';
import { scriptPaths, scriptPathKey, type ScriptPaths } from './script-paths';
import { t, registerMessages } from '../app/locale';
registerMessages({ '客户端由脚本自身启动': 'The script launches its own client' });
import EmulatorFields from './EmulatorFields.vue';
type Script = AutomationBundle['scripts'][number];
type Target = NonNullable<Script['target']>;
const props = defineProps<{ modelValue: Script; store: AutomationStore }>();
const emit = defineEmits<{ 'update:modelValue': [value: Script] }>();
const paths = computed(() => scriptPaths(props.store, props.modelValue));
const available = useAdapterAvailability(props.store, () => props.modelValue.extensionId || null, () => true, () => {});
const policyPending = computed(() => props.modelValue.kind === 'specialized' && !available.details.value);
const policy = computed(() => available.details.value?.manifest.specialized?.instancePolicy);
const selfManagedPc = computed(() => policy.value?.selfManagedPcLaunch === true && target.value.kind === 'pc');
watch([policy, () => props.modelValue.target?.kind], () => {
  const rule = policy.value;
  if (!rule) return;
  const next = { ...props.modelValue, updateConfigurationAtFinish: rule.requiresConfigurationSync ? true : props.modelValue.updateConfigurationAtFinish };
  if (rule.selfManagedPcLaunch && next.target?.pc) {
    next.target = { ...next.target, launchByHost: false, waitSeconds: 0, pc: { ...next.target.pc, arguments: [] } };
    paths.value.gameArguments = '';
  }
  if (JSON.stringify(next) !== JSON.stringify(props.modelValue)) emit('update:modelValue', next);
});
function path(key: Exclude<keyof ScriptPaths, 'judgeLanguage' | 'emulatorRoot'>, value: string): void {
  paths.value[key] = value;
  props.store.dirty.add(scriptPathKey(props.modelValue.id));
  if (!props.modelValue.target) update({});
  if (key === 'androidTool' && target.value.android?.emulator) {
    const { emulator: omitted, ...manual } = target.value.android;
    update({ android: manual });
  }
}
async function browse(key: Exclude<keyof ScriptPaths, 'judgeLanguage' | 'emulatorRoot'>): Promise<void> {
  try {
    const result = await desktop?.chooseFile();
    if (result && !result.canceled && result.paths[0]) path(key, result.paths[0]);
  } catch (reason) {
    props.store.report(reason);
  }
}
const target = computed<Target>(
  () =>
    props.modelValue.target || {
      kind: 'pc',
      launchByHost: false,
      forceCloseAtEnd: props.modelValue.kind === 'specialized',
      waitSeconds: 30,
      pc: {
        launchExecutable: { rootId: props.modelValue.rootId, relative: '' },
        readyExecutable: { rootId: props.modelValue.rootId, relative: '' },
        arguments: [],
        readinessMode: 'process'
      }
    }
);
function update(value: Partial<Target>): void {
  emit('update:modelValue', { ...props.modelValue, target: { ...target.value, ...value } });
}
function mode(value: string): void {
  if (value === target.value.kind || value === 'android' && policy.value?.supportsAndroid === false) return;
  const common = {
    launchByHost: target.value.launchByHost,
    forceCloseAtEnd: target.value.forceCloseAtEnd,
    waitSeconds: target.value.waitSeconds
  };
  const path = { rootId: props.modelValue.rootId, relative: '' };
  emit('update:modelValue', {
    ...props.modelValue,
    target:
      value === 'pc'
        ? {
            ...common,
            kind: 'pc',
            pc: {
              launchExecutable: path,
              readyExecutable: path,
              arguments: [],
              readinessMode: 'process'
            }
          }
        : {
            ...common,
            kind: 'android',
            android: {
              driver: 'genericAdb',
              tool: path,
              endpoint: '127.0.0.1:5555',
              package: '',
              startArguments: []
            }
          }
  });
}
function pcPath(value: string): void {
  path('gamePath', value);
  if (!target.value.pc) return;
  const game = { rootId: props.modelValue.rootId, relative: value };
  update({ pc: { ...target.value.pc, launchExecutable: game, readyExecutable: game } });
}
function android(key: string, value: string): void {
  if (target.value.android) {
    const { emulator, ...manual } = target.value.android;
    update({ android: { ...manual, ...(key !== 'endpoint' && emulator ? { emulator } : {}), [key]: value } });
  }
}
</script>

<template>
  <section class="game-fields">
    <h3>{{ t('游戏联动设置') }}</h3>
    <div class="switch-grid">
      <nexus-switch-setting
        :title="t('启动游戏')"
        :description="t('任务开始前主动启动游戏')"
        :value.prop="selfManagedPc ? false : target.launchByHost"
        :disabled.prop="policyPending || selfManagedPc"
        @nexus-change="update({ launchByHost: $event.detail.value })"
      />
      <nexus-switch-setting
        :title="t('强制关闭')"
        :description="t('仅清理本次持有的游戏进程')"
        :value.prop="target.forceCloseAtEnd"
        @nexus-change="update({ forceCloseAtEnd: $event.detail.value })"
      />
      <nexus-switch-setting
        :title="t('自动更新配置')"
        :description="t('运行结束时同步用户配置')"
        :value.prop="modelValue.updateConfigurationAtFinish"
        :disabled.prop="policyPending || policy?.requiresConfigurationSync === true"
        @nexus-change="
          emit('update:modelValue', { ...modelValue, updateConfigurationAtFinish: $event.detail.value })
        "
      />
    </div>
    <p v-if="selfManagedPc">{{ t('客户端由脚本自身启动') }}</p>
    <div class="game-panel">
      <template v-if="target.kind === 'pc'">
        <label>
          {{ t('游戏路径') }}
          <em v-if="target.launchByHost || target.forceCloseAtEnd">*</em>
          <nexus-path-picker
            :aria-label="t('游戏路径')"
            :aria-required="target.launchByHost || target.forceCloseAtEnd"
            :value.prop="paths.gamePath"
            :placeholder="t('游戏可执行文件路径')"
            :kinds.prop="['file']"
            :browse-allowed.prop.camel="!!desktop"
            @nexus-change="pcPath($event.detail.value)"
            @nexus-browse="browse('gamePath')"
          />
        </label>
        <label>
          {{ t('脚本自启动参数') }}
          <nexus-text-input
            :aria-label="t('游戏启动参数')"
            :value.prop="paths.gameArguments"
            :disabled.prop="policyPending || selfManagedPc"
            @nexus-change="path('gameArguments', $event.detail.value)"
          />
        </label>
      </template>
      <template v-else>
        <EmulatorFields :model-value="modelValue" :store="store" @update:model-value="emit('update:modelValue', $event)" />
        <label>
          {{ t('ADB 程序路径') }}
          <nexus-path-picker
            :aria-label="t('ADB 程序路径')"
            :kinds.prop="['file']"
            :value.prop="paths.androidTool"
            :browse-allowed.prop.camel="!!desktop"
            @nexus-change="path('androidTool', $event.detail.value)"
            @nexus-browse="browse('androidTool')"
          />
        </label>
        <label>
          {{ t('ADB 地址') }}
          <nexus-text-input
            :aria-label="t('ADB 地址')"
            :value.prop="target.android?.endpoint || ''"
            @nexus-change="android('endpoint', $event.detail.value)"
          />
        </label>
        <label>
          {{ t('游戏包名') }}
          <nexus-text-input
            :aria-label="t('游戏包名')"
            :value.prop="target.android?.package || ''"
            @nexus-change="android('package', $event.detail.value)"
          />
        </label>
      </template>
      <label>
        {{ t('启动方式') }}
        <FormSelect :value="target.kind" @change="mode(($event.target as HTMLSelectElement).value)">
          <option value="pc">{{ t('PC 客户端') }}</option>
          <option value="android" :disabled="policy?.supportsAndroid === false">{{ t('安卓模拟器') }}</option>
        </FormSelect>
      </label>
      <label>
        {{ t('启动后等待秒数') }}
        <nexus-number-input
          :aria-label="t('启动后等待秒数')"
          :min.prop="0"
          :max.prop="604800"
          :value.prop="target.waitSeconds"
          :disabled.prop="policyPending || selfManagedPc"
          @nexus-change="update({ waitSeconds: $event.detail.value })"
        />
      </label>
    </div>
  </section>
</template>

<style scoped>
.game-fields {
  border-top: 1px solid var(--border);
  padding-top: 20px;
  margin-top: -4px;
}
h3 {
  font-size: 15px;
  line-height: 1.35;
  margin: 0 0 14px;
}
.switch-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
}
.switch-grid nexus-switch-setting {
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 12px 16px;
  background: var(--content-control);
}
.game-panel {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px 16px;
  padding: 12px;
  margin-top: 12px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--content-control);
}
label {
  display: flex;
  flex-wrap: wrap;
  align-content: start;
  gap: 6px 2px;
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
}
label > :not(em) {
  flex-basis: 100%;
  width: 100%;
}
em {
  color: var(--bad);
  font-style: normal;
}
@media (max-width: 650px) {
  .switch-grid,
  .game-panel {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
