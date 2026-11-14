<script setup lang="ts">
import { t } from '../app/locale';
import type { AutomationBundle } from '../control/wire.generated';

type Limits = AutomationBundle['scripts'][number]['limits'];
const props = defineProps<{ modelValue: Limits }>();
const emit = defineEmits<{ 'update:modelValue': [value: Limits] }>();
function limit(key: keyof Limits, value: number): void {
  emit('update:modelValue', { ...props.modelValue,
    [key]: key === 'maximumAttempts' ? value : Math.round(value * 60) });
}
</script>
<template>
    <section class="script-section">
      <h3>{{ t('运行设置') }}</h3>
      <div class="columns three">
        <label>
          {{ t('最大尝试次数（含首次）') }}
          <em>*</em>
          <nexus-number-input
        data-draft-field="ScriptFields/modelValue.maximumAttempts"
            :aria-label="t('最大尝试次数（含首次）')"
            aria-required="true"
            :min.prop="1"
            :max.prop="99"
            :value.prop="modelValue.maximumAttempts"
            @nexus-change="limit('maximumAttempts', $event.detail.value)"
          />
        </label>
        <label>
          {{ t('日志无更新上限（分钟）') }}
          <em>*</em>
          <nexus-number-input
        data-draft-field="ScriptFields/modelValue.stallTimeoutSeconds / 60"
            :aria-label="t('日志无更新上限（分钟）')"
            aria-required="true"
            :min.prop="0"
            :max.prop="1440"
            :value.prop="modelValue.stallTimeoutSeconds / 60"
            @nexus-change="limit('stallTimeoutSeconds', $event.detail.value)"
          />
        </label>
        <label>
          {{ t('运行总时间上限（分钟）') }}
          <em>*</em>
          <nexus-number-input
        data-draft-field="ScriptFields/modelValue.totalTimeoutSeconds / 60"
            :aria-label="t('运行总时间上限（分钟）')"
            aria-required="true"
            :min.prop="0"
            :max.prop="10080"
            :value.prop="modelValue.totalTimeoutSeconds / 60"
            @nexus-change="limit('totalTimeoutSeconds', $event.detail.value)"
          />
        </label>
      </div>
    </section>
</template>
<style scoped src="./script-fields.css"></style>
