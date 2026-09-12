<script setup lang="ts">
import { t } from "../../../platform/i18n";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpModal from "../../../ui/primitives/NxpModal.vue";
import type { DispatchPlanResult, DispatchPlanUser } from "../utils/dispatchTypes";

/** 运行计划弹窗：展示宿主 explain 结果的准入、任务与用户资格。关闭与「取消」同为 emit close。 */

const props = defineProps<{ plan: DispatchPlanResult | null }>();

const emit = defineEmits<{ close: [] }>();

function explainQueueClass(value?: string) {
  const normalized = String(value || "").toLowerCase().replaceAll("-", "_");
  if (normalized === "emulatoronly" || normalized === "emulator_only") return t("dispatch.queue_class_emulator_only");
  if (normalized === "standard") return t("dispatch.queue_class_standard");
  return value || t("dispatch.not_tracked");
}
function explainCompletionAction(value?: string) {
  const labels: Record<string, string> = { none: "common.no_action", exit: "common.exit_application", sleep: "common.sleep", reboot: "common.restart", shutdown: "common.shut_down" };
  return labels[String(value || "none").toLowerCase()] ? t(labels[String(value || "none").toLowerCase()]) : value || t("common.no_action");
}
function explainUserStatus(user: DispatchPlanUser): { tone: "ok" | "blue" | "bad"; label: string; today: string; reason: string } {
  const value = user.status || "ready";
  const tone = value === "ready" ? "ok" : value === "skipped" ? "blue" : "bad";
  const label = value === "ready" ? t("dispatch.ready") : value === "skipped" ? t("dispatch.will_skip") : t("dispatch.blocked");
  const today = Number.isInteger(user.successfulRunsToday)
    ? t("dispatch.plan.successful_today", { successful: user.successfulRunsToday, maximum: (user.maxSuccessfulRunsPerDay || 0) > 0 ? user.maxSuccessfulRunsPerDay : t("common.unlimited") })
    : t("dispatch.not_tracked");
  const reason = t(`dispatch.reason.${user.reasonCode || "ready"}`, user.reasonArgs || {}, user.reasonCode || t("dispatch.ready"));
  return { tone, label, today, reason };
}
function explainReason(code?: string, args?: Record<string, unknown>) {
  return t(`dispatch.warning.${code || ""}`, args || {}, code || t("dispatch.needs_attention"));
}
</script>

<template>
  <NxpModal
    :open="Boolean(props.plan)"
    :title="t('dispatch.run_plan_check')"
    size="wide"
    panel-class="secondary-surface"
    :close-label="t('common.close')"
    @close="emit('close')"
  >
    <template v-if="props.plan">
      <section class="execution-plan" data-testid="execution-explain-result" role="region" :aria-label="t('dispatch.run_plan_check')">
        <section class="execution-plan-summary">
          <div class="execution-plan-summary-main">
            <span class="execution-plan-summary-label">{{ t("dispatch.target") }}</span>
            <strong>{{ props.plan.targetName || "" }}</strong>
            <NxpBadge :tone="props.plan.admissible ? 'ok' : 'bad'">{{ props.plan.admissible ? t("dispatch.ready_to_run") : t("dispatch.cannot_start_now") }}</NxpBadge>
          </div>
          <div class="execution-plan-summary-stat">
            <span class="k">{{ t("common.task") }}</span>
            <strong class="execution-plan-stat-value">{{ t("common.unit.tasks", { count: Number.isFinite(Number(props.plan.totalTasks)) ? Number(props.plan.totalTasks) : (props.plan.tasks || []).length }) }}</strong>
            <span class="muted execution-plan-stat-subvalue">{{ explainQueueClass(props.plan.queueClass) }}</span>
          </div>
          <div class="execution-plan-summary-stat">
            <span class="k">{{ t("common.completion_action") }}</span>
            <strong class="execution-plan-stat-value">{{ explainCompletionAction(props.plan.completionAction) }}</strong>
          </div>
        </section>
        <div v-if="props.plan.admissionFailure" class="callout callout-warning execution-plan-warning">
          <strong>{{ t(`api.error.${props.plan.admissionFailure.code || 'admission_failed'}`, props.plan.admissionFailure.args || {}, props.plan.admissionFailure.code || t('dispatch.start.unavailable')) }}</strong>
        </div>
        <section v-if="props.plan.tasks?.length" class="execution-plan-section" role="table">
          <div class="execution-plan-section-heading">
            <div class="execution-plan-section-heading-main">
              <h4>{{ t("common.task_list") }}</h4>
              <NxpBadge tone="muted">{{ t("common.unit.tasks", { count: props.plan.tasks.length }) }}</NxpBadge>
            </div>
          </div>
          <div class="execution-plan-table-header execution-plan-task-header" role="row">
            <span role="columnheader">{{ t("common.task") }}</span>
            <span role="columnheader">{{ t("dispatch.users") }}</span>
          </div>
          <div v-for="(task, index) in props.plan.tasks" :key="`${task.taskId || task.scriptName}-${index}`" class="execution-plan-row execution-plan-task-row" role="row">
            <div class="execution-plan-cell execution-plan-name" role="cell">
              <div class="execution-plan-task-main">
                <span class="execution-plan-task-index" aria-hidden="true">{{ index + 1 }}</span>
                <span class="execution-plan-task-copy"><strong>{{ task.scriptName || task.taskId || "" }}</strong></span>
              </div>
            </div>
            <div class="execution-plan-cell execution-plan-users" role="cell">
              <NxpBadge tone="blue">{{ t("dispatch.summary.users", { count: task.userCount || 0 }) }}</NxpBadge>
            </div>
          </div>
        </section>
        <section v-if="props.plan.users?.length" class="execution-plan-section" role="table">
          <div class="execution-plan-section-heading">
            <div class="execution-plan-section-heading-main">
              <h4>{{ t("dispatch.user_eligibility") }}</h4>
              <NxpBadge tone="muted">{{ t("dispatch.summary.users", { count: props.plan.users.length }) }}</NxpBadge>
            </div>
          </div>
          <div class="execution-plan-table-header execution-plan-user-header" role="row">
            <span role="columnheader">{{ t("common.user") }}</span>
            <span role="columnheader">{{ t("common.status") }}</span>
            <span role="columnheader">{{ t("dispatch.successful_today") }}</span>
            <span role="columnheader">{{ t("common.reason") }}</span>
          </div>
          <div v-for="(user, index) in props.plan.users" :key="`${user.userName}-${index}`" class="execution-plan-row execution-plan-user-row" role="row">
            <div class="execution-plan-cell execution-plan-name" role="cell">{{ user.userName || t("dispatch.unnamed_user") }}</div>
            <div class="execution-plan-cell execution-plan-status" role="cell">
              <NxpBadge :tone="explainUserStatus(user).tone">{{ explainUserStatus(user).label }}</NxpBadge>
            </div>
            <div class="execution-plan-cell execution-plan-today muted" role="cell">{{ explainUserStatus(user).today }}</div>
            <div class="execution-plan-cell execution-plan-reason muted" role="cell">{{ explainUserStatus(user).reason }}</div>
          </div>
        </section>
        <div v-if="props.plan.warnings?.length" class="callout callout-warning execution-plan-warning">
          <strong>{{ t("dispatch.notice") }}</strong><br>
          <span v-for="(warning, index) in props.plan.warnings" :key="`${warning.code}-${index}`">{{ explainReason(warning.code, warning.args) }}<br></span>
        </div>
      </section>
    </template>
  </NxpModal>
</template>
