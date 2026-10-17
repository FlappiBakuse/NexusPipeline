<script setup lang="ts">
import { computed } from "vue";
import { t } from "../../../platform/i18n";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpModal from "../../../ui/primitives/NxpModal.vue";
import NxpEmptyState from "../../../ui/primitives/NxpEmptyState.vue";
import type { DispatchPlanResult, DispatchPlanUser } from "../utils/dispatchTypes";

const props = defineProps<{ plan: DispatchPlanResult | null }>();
const emit = defineEmits<{ close: [] }>();
type State = "ready" | "attention" | "blocked";
const tone = (state: State) => state === "ready" ? "ok" : state === "attention" ? "warn" : "bad";
const label = (state: State) => t(`dispatch.plan.${state}`);
function userState(user: DispatchPlanUser): State {
  return user.status === "ready" ? "ready" : user.status === "skipped" ? "attention" : "blocked";
}
function userReason(user: DispatchPlanUser) {
  return t(`dispatch.reason.${user.reasonCode || "ready"}`, user.reasonArgs || {}, user.reasonCode || t("dispatch.reason.ready"));
}
function warningText(warning: { code?: string; args?: Record<string, unknown> }) {
  return t(`dispatch.warning.${warning.code || ""}`, warning.args || {}, warning.code || t("dispatch.needs_attention"));
}
const tasks = computed(() => (props.plan?.tasks || []).map(task => {
  const users = (props.plan?.users || []).filter(user => user.scriptId === task.scriptId);
  const uniqueUsers = users.filter((user, index) => users.findIndex(other => (other.userId || other.userName) === (user.userId || user.userName)) === index);
  const warnings = (props.plan?.warnings || []).filter(warning => warning.args?.taskId ? warning.args.taskId === task.taskId : warning.args?.scriptId === task.scriptId);
  // User eligibility already explains these failures; retain warnings not represented by a user row.
  const notes = [...new Set(warnings.filter(warning => !uniqueUsers.some(user => user.reasonCode === warning.code)).map(warningText))];
  if (!uniqueUsers.length && !notes.length) notes.push(t("dispatch.plan.no_users"));
  const ready = uniqueUsers.filter(user => userState(user) === "ready").length;
  const state: State = !ready && (!uniqueUsers.length || uniqueUsers.some(user => userState(user) === "blocked")) ? "blocked"
    : warnings.length || uniqueUsers.some(user => userState(user) !== "ready") ? "attention" : "ready";
  return { ...task, users: uniqueUsers, notes, state };
}));
const overall = computed<State>(() => !props.plan?.admissible || !tasks.value.some(task => task.users.some(user => userState(user) === "ready")) ? "blocked"
  : tasks.value.some(task => task.state !== "ready") || props.plan?.warnings?.length ? "attention" : "ready");
const globalWarnings = computed(() => (props.plan?.warnings || []).filter(warning => !tasks.value.some(task => warning.args?.taskId ? warning.args.taskId === task.taskId : warning.args?.scriptId === task.scriptId)));
const completionAction = computed(() => {
  const action = props.plan?.completionAction || "none";
  const keys: Record<string, string> = { exit: "common.exit_application", sleep: "common.sleep", reboot: "common.restart", shutdown: "common.shut_down" };
  return action === "none" ? "" : t(keys[action] || "", {}, action);
});
</script>

<template>
  <NxpModal :open="Boolean(plan)" :title="t('dispatch.run_plan_check')" size="wide" panel-class="secondary-surface" :close-label="t('common.close')" @close="emit('close')">
    <section v-if="plan" class="assessment-layout" data-testid="execution-explain-result" :aria-label="t('dispatch.run_plan_check')">
      <header class="assessment-overview" data-testid="execution-plan-summary">
        <div class="assessment-overview-title"><strong>{{ plan.targetName }}</strong><NxpBadge :tone="tone(overall)">{{ label(overall) }}</NxpBadge></div>
        <p v-if="plan.admissionFailure" class="execution-plan-admission">{{ t(`api.error.${plan.admissionFailure.code || 'admission_failed'}`, plan.admissionFailure.args || {}, plan.admissionFailure.code || t('dispatch.start.unavailable')) }}</p>
        <p v-else>{{ t(`dispatch.plan.summary_${overall}`) }}</p>
        <p v-if="completionAction">{{ t('common.completion_action') }}：{{ completionAction }}</p>
      </header>
      <NxpEmptyState v-if="!tasks.length" :title="t('dispatch.plan.no_tasks')" />
      <section v-for="(task, index) in tasks" :key="`${task.taskId}-${index}`" class="assessment-group" data-testid="execution-plan-task">
        <header class="assessment-group-heading"><span class="assessment-index" aria-hidden="true">{{ index + 1 }}</span><h4>{{ task.scriptName || t('dispatch.plan.missing_script') }}</h4><NxpBadge :tone="tone(task.state)">{{ label(task.state) }}</NxpBadge></header>
        <div v-if="task.users.length" class="execution-plan-eligibility" role="table" :aria-label="task.scriptName || t('dispatch.user_eligibility')">
          <div class="assessment-row assessment-table-header" role="row"><span role="columnheader">{{ t('common.user') }}</span><span role="columnheader">{{ t('common.status') }}</span><span role="columnheader">{{ t('common.reason') }}</span></div>
          <div v-for="(user, userIndex) in task.users" :key="user.userId || userIndex" class="assessment-row" role="row">
            <div class="execution-plan-account" role="cell"><strong>{{ user.userName || t('dispatch.unnamed_user') }}</strong><span v-if="Number.isInteger(user.successfulRunsToday) && (user.maxSuccessfulRunsPerDay || 0) > 0" class="muted">{{ t('dispatch.plan.daily_limit', { successful: user.successfulRunsToday, maximum: user.maxSuccessfulRunsPerDay }) }}</span></div>
            <div class="assessment-outcome" role="cell"><NxpBadge :tone="tone(userState(user))">{{ user.status === 'skipped' ? t('dispatch.will_skip') : label(userState(user)) }}</NxpBadge></div>
            <div class="assessment-explanation" role="cell">{{ userReason(user) }}</div>
          </div>
        </div>
        <ul v-if="task.notes.length" class="execution-plan-notes"><li v-for="note in task.notes" :key="note">{{ note }}</li></ul>
      </section>
      <ul v-if="globalWarnings.length" class="execution-plan-notes" data-testid="execution-plan-warnings"><li v-for="(warning, index) in globalWarnings" :key="index">{{ warningText(warning) }}</li></ul>
    </section>
  </NxpModal>
</template>
