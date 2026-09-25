<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from "vue";
import { api } from "../../../platform/api";
import { t } from "../../../platform/i18n";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpCollapsibleCard from "../../../ui/composites/NxpCollapsibleCard.vue";
import TaskReportPanel from "../../history/components/TaskReportPanel.vue";
import { toast } from "../../../platform/toast";
import type { TaskPlan } from "../../history/utils/taskTypes";
const props = defineProps<{ userId: string; scriptId: string; revision?: number }>();
const emit = defineEmits<{ action: [kind: string] }>();
const result = ref<{ plan?: TaskPlan; error?: string; stale?: boolean }>();
const loading = ref(false);
const expanded = ref(false);
type RepairPreview = { available: boolean; plugin: string; userId: string; scriptId: string; field: string; oldValue: string; proposedValue: string; impact: string; token: string };
const repair = ref<RepairPreview | null>(null);
const repairError = ref("");
const repairBusy = ref(false);
function toggle() { expanded.value = !expanded.value; if (expanded.value && !result.value && !loading.value) void load(); }
let generation = 0;
let controller: AbortController | null = null;
watch(() => [props.userId, props.scriptId, props.revision], () => { generation++; controller?.abort(); result.value = undefined; repair.value = null; repairError.value = ""; loading.value = false; if (expanded.value) void load(); });
async function load() {
  const own = ++generation; loading.value = true;
  controller?.abort(); controller = new AbortController();
  try {
    const data = await api("GET", `/api/users/${encodeURIComponent(props.userId)}/bindings/${encodeURIComponent(props.scriptId)}/task-plan`, undefined, controller.signal);
    if (generation === own) result.value = data as typeof result.value;
  } catch { if (generation === own) result.value = { error: "config_unavailable" }; }
  finally { if (generation === own) loading.value = false; }
}
function handleConfigAction(kind: string) {
  if (kind === "refresh_plan") {
    void load();
    return;
  }
  emit("action", kind);
}
function repairPath() { return `/api/users/${encodeURIComponent(props.userId)}/bindings/${encodeURIComponent(props.scriptId)}/config-repair`; }
async function previewRepair() {
  repair.value = null; repairError.value = ""; repairBusy.value = true;
  try { repair.value = await api<RepairPreview>("GET", repairPath()); }
  catch (reason) { repairError.value = reason instanceof Error ? reason.message : String(reason); }
  finally { repairBusy.value = false; }
}
async function applyRepair() {
  if (!repair.value?.token) return;
  repairBusy.value = true; repairError.value = "";
  try {
    await api("POST", repairPath(), { token: repair.value.token });
    repair.value = null;
    toast(t("tasks.repair_applied"));
    await load();
  } catch (reason) {
    repair.value = null;
    repairError.value = reason instanceof Error ? reason.message : String(reason);
  } finally { repairBusy.value = false; }
}
onBeforeUnmount(() => { generation++; controller?.abort(); });
</script>
<template>
  <NxpCollapsibleCard :title="t('tasks.preview')" :description="t('tasks.preview_help')" :expanded="expanded" @toggle="toggle">
      <p v-if="loading" role="status">{{ t('common.loading') }}</p>
      <p v-if="result?.error" role="status">{{ t(`tasks.error.${result.error}`) }}</p>
      <TaskReportPanel v-if="result?.plan" :plan="result.plan" :stale="result.stale" :config-action="handleConfigAction" />
      <div v-if="repair" class="repair-preview" role="group" :aria-label="t('tasks.repair_preview')">
        <p>{{ t('tasks.repair_scope', { plugin: repair.plugin, user: repair.userId, field: repair.field }) }}</p>
        <p>{{ t('tasks.repair_reason') }}</p>
        <p>{{ repair.oldValue }} → {{ repair.proposedValue }}</p>
        <p>{{ repair.impact }}</p>
        <NxpButton type="button" :disabled="repairBusy" @click="applyRepair">{{ t('tasks.repair_apply') }}</NxpButton>
      </div>
      <p v-if="repairError" role="status">{{ repairError }}</p>
      <footer v-if="result" class="task-plan-actions">
        <NxpButton class="ghost" type="button" :disabled="loading || repairBusy" @click="previewRepair">{{ t('tasks.repair_preview') }}</NxpButton>
        <NxpButton class="ghost" type="button" :disabled="loading" @click="load">{{ t('tasks.refresh') }}</NxpButton>
      </footer>
  </NxpCollapsibleCard>
</template>
<style scoped>
.task-plan-actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
.repair-preview { margin-top: 16px; padding: 12px; border: 1px solid var(--nxp-border, #777); border-radius: 8px; }
</style>
