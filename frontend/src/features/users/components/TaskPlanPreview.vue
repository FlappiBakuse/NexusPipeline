<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from "vue";
import { api } from "../../../platform/api";
import { t } from "../../../platform/i18n";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpCollapsibleCard from "../../../ui/composites/NxpCollapsibleCard.vue";
import TaskReportPanel from "../../history/components/TaskReportPanel.vue";
import type { TaskPlan } from "../../history/utils/taskTypes";
const props = defineProps<{ userId: string; scriptId: string }>();
const result = ref<{ plan?: TaskPlan; error?: string; stale?: boolean }>();
const loading = ref(false);
const expanded = ref(false);
function toggle() { expanded.value = !expanded.value; if (expanded.value && !result.value && !loading.value) void load(); }
let generation = 0;
let controller: AbortController | null = null;
watch(() => [props.userId, props.scriptId], () => { generation++; controller?.abort(); result.value = undefined; loading.value = false; if (expanded.value) void load(); });
async function load() {
  const own = ++generation; loading.value = true;
  controller?.abort(); controller = new AbortController();
  try {
    const data = await api("GET", `/api/users/${encodeURIComponent(props.userId)}/bindings/${encodeURIComponent(props.scriptId)}/task-plan`, undefined, controller.signal);
    if (generation === own) result.value = data as typeof result.value;
  } catch { if (generation === own) result.value = { error: "config_unavailable" }; }
  finally { if (generation === own) loading.value = false; }
}
onBeforeUnmount(() => { generation++; controller?.abort(); });
</script>
<template>
  <NxpCollapsibleCard :title="t('tasks.preview')" :description="t('tasks.preview_help')" :expanded="expanded" @toggle="toggle">
      <p v-if="loading" role="status">{{ t('common.loading') }}</p>
      <p v-if="result?.error" role="status">{{ t(`tasks.error.${result.error}`) }}</p>
      <TaskReportPanel v-if="result?.plan" :plan="result.plan" :stale="result.stale" />
      <footer v-if="result" class="task-plan-actions"><NxpButton class="ghost" type="button" :disabled="loading" @click="load">{{ t('tasks.refresh') }}</NxpButton></footer>
  </NxpCollapsibleCard>
</template>
<style scoped>
.task-plan-actions { display: flex; justify-content: flex-end; margin-top: 16px; }
</style>
