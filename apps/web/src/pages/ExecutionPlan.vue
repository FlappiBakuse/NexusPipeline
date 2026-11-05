<script setup lang="ts">
import { computed } from 'vue';
import type { AutomationBundle } from '../control/wire.generated';
import type { ExecutionPreview } from '../control/execution';
import { t } from '../app/locale';
import { completionLabels, planAccounts, planLabel, planReason, planState, planTone } from './execution-plan';

const props = defineProps<{
  preview: ExecutionPreview;
  bundle?: AutomationBundle;
  targetName: string;
  targetKind: string;
  targetId: string;
}>();
const accounts = computed(() => planAccounts(props.preview));
const groups = computed(() => {
  const declared = props.preview.groups.map(value => value.scriptId);
  const scripts = declared.length
    ? declared
    : props.targetKind === 'script'
      ? [props.targetId]
      : props.bundle?.queues.find((queue) => queue.id === props.targetId)?.scriptIds || [];
  return scripts.map((id, index) => {
    const matching = accounts.value.filter(item => item.scriptId === id);
    const count = matching.length / scripts.filter(script => script === id).length;
    const prior = scripts.slice(0, index).filter(script => script === id).length;
    const users = matching.slice(prior * count, (prior + 1) * count);
    return { id: id + ':' + index,
      name: props.bundle?.scripts.find(script => script.id === id)?.name || id, users, state: planState(users) };
  });
});
const overall = computed(() =>
  props.preview.hasRunnable !== true
    ? 'blocked'
    : groups.value.some((group) => group.state !== 'ready')
      ? 'attention'
      : 'ready'
);
const summary = computed(() =>
  overall.value === 'ready'
    ? '当前检查通过，各账号符合运行条件。'
    : overall.value === 'attention'
      ? '部分任务将跳过或存在需要留意的事项，请查看下方原因。'
      : '当前没有可正常执行的计划，请先处理下方问题。'
);
const completion = computed(() => completionLabels[props.preview.completionAction]);
</script>

<template>
  <section class="plan-assessment" :aria-label="t('运行计划检查')">
    <header class="plan-summary">
      <div>
        <strong>{{ targetName }}</strong>
        <nexus-badge :tone="planTone(overall)" :label="planLabel(overall)" />
      </div>
      <p>{{ t(summary) }}</p>
      <p v-if="completion">{{ t('运行完成操作') }}：{{ t(completion) }}</p>
    </header>
    <nexus-empty-state v-if="!groups.length" :title="t('此计划没有可执行的任务。')" />
    <article v-for="(group, index) in groups" :key="group.id" class="plan-group">
      <header>
        <span class="plan-number">{{ index + 1 }}</span>
        <h4>{{ group.name }}</h4>
        <nexus-badge :tone="planTone(group.state)" :label="planLabel(group.state)" />
      </header>
      <div v-if="group.users.length" role="table" :aria-label="group.name">
        <div class="plan-line plan-columns" role="row">
          <span role="columnheader">{{ t('用户') }}</span>
          <span role="columnheader">{{ t('状态') }}</span>
          <span role="columnheader">{{ t('原因') }}</span>
        </div>
        <div v-for="user in group.users" :key="user.userId" class="plan-line" role="row">
          <div class="plan-account" role="cell">
            <strong>{{ bundle?.users.find((item) => item.id === user.userId)?.name || user.userId }}</strong>
            <small v-if="user.successfulRunsToday != null && user.maximumSuccessfulRunsPerDay != null && user.maximumSuccessfulRunsPerDay >= 0">
              {{ t('今日成功') }} {{ user.successfulRunsToday }} / {{ user.maximumSuccessfulRunsPerDay }}
              {{ t('次') }}
            </small>
          </div>
          <div class="plan-outcome" role="cell">
            <nexus-badge
              :tone="planTone(user.state)"
              :label="user.skipped ? t('将跳过') : planLabel(user.state)"
            />
          </div>
          <div class="plan-reason" role="cell">{{ user.reason }}</div>
        </div>
      </div>
      <p v-else class="plan-note">{{ t('没有可用的账号绑定，请启用账号及其脚本实例。') }}</p>
    </article>
    <p v-if="!groups.length && preview.reasonCode" class="plan-note">
      {{ planReason(String(preview.reasonCode)) }}
    </p>
  </section>
</template>

<style scoped src="./execution-plan.css"></style>
