<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from "vue";
import { api, isAbortError, type ApiError } from "../../../platform/api";
import { t } from "../../../platform/i18n";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpCollapsibleCard from "../../../ui/composites/NxpCollapsibleCard.vue";
import TaskReportPanel from "../../history/components/TaskReportPanel.vue";
import { toast } from "../../../platform/toast";
import type { TaskPlan } from "../../history/utils/taskTypes";
const props = defineProps<{ userId: string; scriptId: string; revision?: number }>();
const result = ref<{ plan?: TaskPlan; error?: string; stale?: boolean }>();
const loading = ref(false);
const expanded = ref(false);
type RepairPreview = { available: boolean; plugin: string; userId: string; scriptId: string; field: string; oldValue: string; proposedValue: string; impact: string; token: string };
const repair = ref<RepairPreview | null>(null);
const repairBusy = ref(false);
const repairAvailable = ref(false);
function toggle() { expanded.value = !expanded.value; if (expanded.value && !result.value && !loading.value) void load(); }
let generation = 0;
let controller: AbortController | null = null;
watch(() => [props.userId, props.scriptId, props.revision], () => { generation++; controller?.abort(); result.value = undefined; repair.value = null; repairAvailable.value = false; repairBusy.value = false; loading.value = false; if (expanded.value) void load(); });
async function load() {
  const own = ++generation; loading.value = true;
  controller?.abort(); controller = new AbortController();
  try {
    const data = await api("GET", `/api/users/${encodeURIComponent(props.userId)}/bindings/${encodeURIComponent(props.scriptId)}/task-plan`, undefined, controller.signal);
    if (generation === own) {
      result.value = data as typeof result.value;
      repairAvailable.value = false;
      try {
        const candidate = await api<RepairPreview>("GET", repairPath(), undefined, controller.signal);
        if (generation === own) repairAvailable.value = candidate.available;
      } catch { /* Unavailable repairs have no actionable entry. */ }
    }
  } catch { if (generation === own) result.value = { error: "config_unavailable" }; }
  finally { if (generation === own) loading.value = false; }
}
function repairPath() { return `/api/users/${encodeURIComponent(props.userId)}/bindings/${encodeURIComponent(props.scriptId)}/config-repair`; }
function notifyRepairResult(reason: unknown) {
  if (isAbortError(reason)) return;
  const kind = (reason as ApiError | null)?.code === "config_repair_none" ? "info" : "error";
  toast(reason instanceof Error ? reason.message : String(reason), kind);
}
async function previewRepair() {
  const own = generation;
  repair.value = null; repairBusy.value = true;
  try {
    const candidate = await api<RepairPreview>("GET", repairPath());
    if (generation !== own) return;
    repairAvailable.value = candidate.available;
    repair.value = candidate.available ? candidate : null;
    if (!candidate.available) toast(t("api.error.config_repair_none"), "info");
  }
  catch (reason) { if (generation === own) notifyRepairResult(reason); }
  finally { if (generation === own) repairBusy.value = false; }
}
async function applyRepair() {
  if (!repair.value?.token) return;
  const user = props.userId, script = props.scriptId;
  repairBusy.value = true;
  try {
    await api("POST", repairPath(), { token: repair.value.token });
    if (props.userId !== user || props.scriptId !== script) return;
    repair.value = null;
    toast(t("tasks.repair_applied"));
    await load();
    if (props.userId === user && props.scriptId === script && repairAvailable.value) await previewRepair();
  } catch (reason) {
    if (props.userId !== user || props.scriptId !== script) return;
    repair.value = null;
    notifyRepairResult(reason);
  } finally { if (props.userId === user && props.scriptId === script) repairBusy.value = false; }
}
onBeforeUnmount(() => { generation++; controller?.abort(); });
</script>
<template>
  <NxpCollapsibleCard :title="t('tasks.preview')" :description="t('tasks.preview_help')" :expanded="expanded" @toggle="toggle">
      <p v-if="loading" role="status">{{ t('common.loading') }}</p>
      <p v-if="result?.error" role="status">{{ t(`tasks.error.${result.error}`) }}</p>
      <TaskReportPanel v-if="result?.plan" :plan="result.plan" :stale="result.stale" />
      <div v-if="repair" class="repair-preview" role="group" :aria-label="t('tasks.repair_preview')">
        <p>{{ t('tasks.repair_scope', { plugin: repair.plugin, user: repair.userId, field: repair.field }) }}</p>
        <p>{{ t('tasks.repair_reason') }}</p>
        <p>{{ repair.oldValue }} → {{ repair.proposedValue }}</p>
        <p>{{ repair.impact }}</p>
        <NxpButton type="button" :disabled="repairBusy" @click="applyRepair">{{ t('tasks.repair_apply') }}</NxpButton>
      </div>
      <footer v-if="result" class="task-plan-actions">
        <NxpButton v-if="repairAvailable" class="ghost" type="button" :disabled="loading || repairBusy" @click="previewRepair">{{ t('tasks.repair_preview') }}</NxpButton>
        <NxpButton class="ghost" type="button" :disabled="loading || repairBusy" @click="load">{{ t('tasks.config.action.refresh_plan') }}</NxpButton>
      </footer>
  </NxpCollapsibleCard>
</template>
<style scoped>
.task-plan-actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
.repair-preview { margin-top: 16px; padding: 12px; border: 1px solid var(--nxp-border, #777); border-radius: 8px; }
</style>
