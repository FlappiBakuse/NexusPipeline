<script setup lang="ts">
import { computed } from 'vue';
import type { ActiveStep } from '../control/execution';
import { t, locale, registerMessages } from '../app/locale';
import { taskTitle } from '../control/extension-metadata';

const props = defineProps<{
  steps: readonly ActiveStep[];
  final?: boolean;
  scriptName?: (id: string) => string;
}>();
registerMessages({
  任务依赖与处理进度: 'Task dependencies and processing progress',
  步已处理: 'steps processed',
  依赖: 'Depends on',
  无依赖: 'No dependencies',
  待宿主收集: 'Awaiting Host observation',
  未选择: 'Not selected',
  已停用: 'Disabled',
  宿主任务事实: 'Host task observations',
  暂无声明任务: 'No declared tasks',
  执行引擎: 'Execution engine',
  业务结果: 'Business outcome',
  原因代码: 'Reason code',
  任务状态: 'Task state',
  准备中: 'Preparing',
  运行中: 'Running',
  等待中: 'Pending',
  已跳过: 'Skipped',
  配置已恢复: 'Configuration restored',
  收集中: 'Collecting',
  已收集: 'Collected',
  恢复配置中: 'Restoring configuration',
  停止中: 'Stopping',
  未执行: 'Not executed',
  成功: 'Succeeded',
  部分完成: 'Partially completed',
  失败: 'Failed',
  结果未知: 'Unknown'
});
const rows = computed(() => props.steps);
const processed = computed(
  () => rows.value.filter((step) => step.phase === 'restored' || step.phase === 'skipped').length
);
const names = computed(
  () =>
    new Map(
      rows.value.map((step, index) => [
        step.stepId,
        `${index + 1}. ${props.scriptName?.(step.scriptId) || step.scriptId}`
      ])
    )
);
const phaseNames: Record<string, string> = {
  preparing: '准备中',
  running: '运行中',
  pending: '等待中',
  skipped: '已跳过',
  restored: '配置已恢复',
  collecting: '收集中',
  collected: '已收集',
  restoring: '恢复配置中',
  stopping: '停止中',
  notExecuted: '未执行',
  succeeded: '成功',
  failed: '失败',
  partial: '部分完成',
  unknown: '结果未知'
};
function phase(value: unknown): string {
  return t(phaseNames[String(value)] || String(value));
}
</script>

<template>
  <section class="task-report" :aria-label="t(final ? '宿主任务事实' : '任务依赖与处理进度')">
    <header>
      <strong>{{ t(final ? '宿主任务事实' : '任务依赖与处理进度') }}</strong>
      <span v-if="!final">{{ processed }}/{{ rows.length }} {{ t('步已处理') }}</span>
    </header>
    <progress
      v-if="!final"
      :value="processed"
      :max="Math.max(1, rows.length)"
      :aria-label="t('任务依赖与处理进度')"
    />
    <ol class="step-tree">
      <li v-for="(step, index) in rows" :key="step.stepId">
        <div class="step-head">
          <strong>{{ index + 1 }}. {{ scriptName?.(step.scriptId) || step.scriptId }}</strong>
          <nexus-badge :label="phase(step.phase)" />
        </div>
        <p class="dependencies">
          {{ t('依赖') }}：{{
            step.dependsOn.length ? step.dependsOn.map((id) => names.get(id) || id).join('、') : t('无依赖')
          }}
        </p>
        <p v-if="final && (step.engine || step.business || step.reasonCode)" class="facts">
          <span v-if="step.engine">{{ t('执行引擎') }}：{{ step.engine }}</span>
          <span v-if="step.business">{{ t('业务结果') }}：{{ step.business }}</span>
          <span v-if="step.reasonCode">{{ t('原因代码') }}：{{ step.reasonCode }}</span>
        </p>
        <details v-if="step.tasks.length">
          <summary>{{ t('宿主任务事实') }} · {{ step.tasks.length }}</summary>
          <ul class="tasks">
            <li v-for="task in step.tasks" :key="String(task.taskId)">
              <strong>{{ taskTitle(task, locale) || task.taskId }}</strong>
              <span>
                {{ t('任务状态') }}：{{
                  task.isSelected === false
                    ? t('未选择')
                    : task.isEnabled === false
                      ? t('已停用')
                      : task.state === 'unknown'
                        ? t('待宿主收集')
                        : phase(task.state)
                }}
              </span>
              <small v-if="task.reasonCode">{{ t('原因代码') }}：{{ task.reasonCode }}</small>
              <small v-if="task.dependencies.length">
                {{ t('依赖') }}：{{ task.dependencies.join('、') }}
              </small>
            </li>
          </ul>
        </details>
        <p v-else class="muted">{{ t('暂无声明任务') }}</p>
      </li>
    </ol>
  </section>
</template>

<style scoped>
.task-report {
  display: grid;
  gap: 10px;
}
.task-report header,
.step-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}
.task-report progress {
  width: 100%;
  height: 9px;
  accent-color: var(--accent);
}
.step-tree,
.tasks {
  display: grid;
  gap: 10px;
  margin: 0;
  padding-left: 25px;
}
.step-tree > li {
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: 9px;
}
.step-tree p {
  margin: 5px 0;
  color: var(--muted);
}
.facts {
  display: flex;
  gap: 12px;
  flex-wrap: wrap;
}
.tasks {
  padding-top: 8px;
}
.tasks li {
  display: grid;
  gap: 3px;
}
.tasks small,
.tasks span,
.muted {
  color: var(--muted);
}
</style>
