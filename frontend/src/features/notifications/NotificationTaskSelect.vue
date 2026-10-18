<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import NxpSelect from "../../ui/primitives/NxpSelect.vue";
import { t } from "../../platform/i18n";
import { isAbortError } from "../../platform/api";
import { loadNotificationsPage } from "./first-screen";
import type { NotificationTask } from "./notificationTypes";
const props = defineProps<{ modelValue?: string[]; disabled?: boolean; id?: string }>();
const emit = defineEmits<{ "update:modelValue": [ids: string[]] }>();
const tasks = ref<NotificationTask[]>([]);
const error = ref("");
const loading = ref(true);
const controller = new AbortController();
const options = computed(() => {
  const items = tasks.value.map(task => ({ value: task.id, label: `${task.name} · ${task.type === "smtp" ? "SMTP" : "Webhook"}${!task.enabled ? ` · ${t("common.disabled")}` : !task.configured ? ` · ${t("notifications.incomplete")}` : ""}` }));
  for (const id of props.modelValue || []) if (!items.some(item => item.value === id))
    items.push({ value: id, label: `${t(loading.value ? "common.loading" : error.value ? "notifications.unavailable" : "notifications.deleted")} · ${id.slice(0, 8)}` });
  return items;
});
onMounted(async () => { try { tasks.value = await loadNotificationsPage(controller.signal); } catch (reason) { if (!isAbortError(reason)) error.value = String(reason); } finally { loading.value = false; } });
onBeforeUnmount(() => controller.abort());
</script>
<template>
  <NxpSelect :id="id" multiple :model-value="modelValue || []" :options="options" :disabled="disabled || loading || !!error" :placeholder="t('notifications.none')" :aria-label="t('notifications.targets')" @update:model-value="emit('update:modelValue', Array.isArray($event) ? $event : [])" />
  <p v-if="error" role="alert" class="muted">{{ t("notifications.load_failed") }}</p>
</template>
