<script setup lang="ts">
import { computed } from 'vue';
import type { AutomationBundle } from '../control/wire.generated';
import type { ExecutionPreview } from '../control/execution';
import { t } from '../app/locale';
import TaskSummary from './TaskSummary.vue';
import { completionLabels, planAccountLabel, planDependency, planGroups, planLabel, planNextStep, planReason, planRound, planTone, type PlanTargetKind } from './execution-plan';

const props = defineProps<{
  preview: ExecutionPreview;
  bundle?: AutomationBundle;
  targetName: string;
  targetKind: PlanTargetKind;
  targetId: string;
}>();
const groups = computed(() => planGroups(props.preview, props.bundle, props.targetKind, props.targetId));
const users = computed(() => new Map(props.bundle?.users.map(item => [item.id, item.name])));
const allSkipped = computed(() => !props.preview.hasBlockedItems && props.preview.items.length > 0 &&
  props.preview.items.every(item => item.disposition === 'skipped'));
const overall = computed(() => {
  if (props.preview.hasBlockedItems) return 'blocked';
  if (!props.preview.hasRunnable) return allSkipped.value ? 'attention' : 'blocked';
  return groups.value.some(group => group.state !== 'ready') ? 'attention' : 'ready';
});
const statusItems = computed(() => groups.value.flatMap(group => group.members.map(account =>
  ({ label: planAccountLabel(account), tone: planTone(account.state) }))));
const completion = computed(() => completionLabels[props.preview.completionAction]);
</script>

<template>
  <section class="plan-assessment" :aria-label="t('运行计划检查')">
    <header class="plan-summary">
      <div class="plan-heading">
        <h3>{{ targetName }}</h3>
        <nexus-badge :label="t(targetKind === 'queue' ? '调度队列' : '脚本实例')" />
        <nexus-badge class="plan-overall" :tone="planTone(overall)" :label="allSkipped ? t('本次全部跳过') : planLabel(overall)" />
      </div>
      <TaskSummary :tasks="statusItems" :label="t('计划状态汇总')" />
      <p v-if="preview.hasBlockedItems" class="plan-note">{{ t('处理阻断项后重新检查计划。') }}</p>
      <p v-if="completion" class="plan-note">{{ t('运行完成操作') }} · {{ t(completion) }}</p>
    </header>

    <nexus-empty-state v-if="!groups.length" :title="planReason(preview.reasonCode ?? 'execution_queue_empty')"
      :description="preview.reasonCode === 'execution_queue_empty' ? t('在计划与队列中加入脚本实例，再重新检查。') : ''" />

    <ol v-else class="plan-timeline" role="list" :aria-label="t('脚本组顺序')">
      <li v-for="group in groups" :key="group.order" class="plan-step">
        <span class="plan-number" aria-hidden="true">{{ group.order }}</span>
        <div class="plan-stage">
          <div v-if="group.dependencies.length" class="plan-dependency" :data-skipped="group.dependencySkipped">
            <span>{{ t('需以下脚本组全部成功') }}</span>
            <strong>{{ group.dependencies.map(item => planDependency(item.order, item.name)).join(' · ') }}</strong>
            <nexus-badge v-if="group.dependencySkipped" tone="warning" :label="t('未满足')" />
          </div>
          <article class="plan-group">
            <header class="plan-heading">
              <h4>{{ group.name }}</h4>
              <nexus-badge v-if="group.round" tone="info" :label="planRound(group.round)" />
              <TaskSummary v-if="group.members.length" class="plan-group-status" :label="t('计划状态汇总')"
                :tasks="group.members.map(account => ({ label: planAccountLabel(account), tone: planTone(account.state) }))" />
              <nexus-badge v-else class="plan-group-status" :label="t('无可用绑定')" />
            </header>
            <p v-if="group.dependencySkipped" class="plan-prerequisite-note">{{ t('前置成功条件未满足，本组将跳过。') }}</p>
            <ol v-if="group.members.length" class="plan-accounts" role="list" :aria-label="t('账号运行顺序')">
              <li v-for="(account, index) in group.members" :key="account.ordinal" class="plan-item"
                :data-ordinal="account.ordinal" :data-binding-id="account.bindingId"
                :data-disposition="account.skipped ? 'skipped' : account.state === 'ready' ? 'runnable' : 'blocked'">
                <div class="plan-account">
                  <span class="plan-position" aria-hidden="true">{{ group.order }}.{{ index + 1 }}</span>
                  <strong>{{ users.get(account.userId) ?? account.userId }}</strong>
                </div>
                <nexus-badge :tone="planTone(account.state)" :label="planAccountLabel(account)" />
                <div v-if="!group.dependencySkipped" class="plan-reason">
                  <span>{{ account.reason }}</span>
                  <span v-if="account.state === 'ready' && account.successfulRunsToday != null && (account.maximumSuccessfulRunsPerDay ?? 0) > 0">
                    · {{ t('今日成功') }} {{ account.successfulRunsToday }}/{{ account.maximumSuccessfulRunsPerDay }} {{ t('次') }}
                  </span>
                  <p v-if="planNextStep(account.reasonCode)">{{ planNextStep(account.reasonCode) }}</p>
                </div>
              </li>
            </ol>
            <p v-else class="plan-group-empty">{{ planReason(group.reasonCode ?? 'execution_no_enabled_bindings') }}</p>
          </article>
        </div>
      </li>
    </ol>
  </section>
</template>

<style scoped>
.plan-assessment {
  display: grid;
  gap: 16px;
  min-inline-size: 0;
  container: execution-plan / inline-size;
}
.plan-summary, .plan-group {
  border-block-start: 1px solid var(--border);
}
.plan-summary {
  display: grid;
  gap: 10px;
  padding: 14px 16px;
}
.plan-heading {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  min-inline-size: 0;
}
.plan-heading :is(h3, h4) { margin: 0; overflow-wrap: anywhere; }
.plan-heading h3 { font-size: 17px; }
.plan-heading h4 { font-size: 14px; }
.plan-overall, .plan-group-status { margin-inline-start: auto; }
.plan-timeline, .plan-accounts { margin: 0; padding: 0; list-style: none; }
.plan-step {
  position: relative;
  display: grid;
  grid-template-columns: 28px minmax(0, 1fr);
  gap: 12px;
  padding-block-end: 12px;
}
.plan-step:last-child { padding-block-end: 0; }
.plan-step:not(:last-child)::before {
  position: absolute;
  content: '';
  inset-block: 24px 0;
  inset-inline-start: 13px;
  border-inline-start: 1px solid var(--nx-color-border);
}
.plan-number {
  z-index: 0;
  display: grid;
  place-items: center;
  block-size: 28px;
  margin-block-start: 9px;
  border-radius: 50%;
  background: var(--content-card-soft);
  border: 1px solid var(--nx-color-border);
  color: var(--accent);
  font-weight: 700;
  box-shadow: var(--nx-surface-shadow);
}
.plan-stage { display: grid; gap: 6px; min-inline-size: 0; }
.plan-dependency {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px 8px;
  padding: 7px 10px;
  border: 1px dashed var(--nx-color-border);
  border-radius: var(--nx-radius-md);
  color: var(--muted);
  font-size: 12px;
  overflow-wrap: anywhere;
  box-shadow: var(--nx-surface-shadow);
}
.plan-dependency[data-skipped='true'] { color: var(--warn); background: var(--warn-soft); }
.plan-dependency nexus-badge { margin-inline-start: auto; }
.plan-group > header { padding: 11px 13px; }
.plan-reason p, .plan-note { margin: 0; }
.plan-note, .plan-group-empty, .plan-reason { color: var(--muted); overflow-wrap: anywhere; }
.plan-group-empty, .plan-prerequisite-note { margin: 0; padding: 9px 13px; border-block-start: 1px solid var(--border); }
.plan-prerequisite-note { color: var(--warn); background: var(--warn-soft); }
.plan-item {
  display: grid;
  grid-template-columns: minmax(0, 1fr) max-content minmax(0, 1.6fr);
  align-items: start;
  gap: 6px 12px;
  padding: 9px 13px;
  border-block-start: 1px solid var(--border);
}
.plan-account {
  display: flex;
  align-items: baseline;
  gap: 9px;
  min-inline-size: 0;
  overflow-wrap: anywhere;
}
.plan-account strong { min-inline-size: 0; }
.plan-position {
  flex: none;
  padding: 2px 5px;
  border: 1px solid var(--border);
  border-radius: var(--nx-radius-compact);
  background: var(--muted-soft);
  color: var(--muted);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
  box-shadow: var(--nx-surface-shadow);
}
.plan-item > nexus-badge { justify-self: start; }
.plan-item[data-disposition='blocked'] .plan-reason { color: var(--bad); }
.plan-reason p { margin-block-start: 2px; color: var(--text); }
@container execution-plan (inline-size < 34rem) {
  .plan-step { gap: 8px; }
  .plan-item { grid-template-columns: minmax(0, 1fr) max-content; }
  .plan-reason { grid-column: 1 / -1; }
}
</style>
