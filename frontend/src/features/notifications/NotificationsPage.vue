<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import { api, isAbortError } from "../../platform/api";
import { t } from "../../platform/i18n";
import { setTopbarTitle } from "../../platform/shell";
import { takePreparedData } from "../../platform/navigation-preparation";
import NxpPageHeader from "../../ui/composites/NxpPageHeader.vue";
import NxpSortableList from "../../ui/composites/NxpSortableList.vue";
import NxpDragHandle from "../../ui/composites/NxpDragHandle.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpOverflowText from "../../ui/primitives/NxpOverflowText.vue";
import NxpModal from "../../ui/primitives/NxpModal.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpCard from "../../ui/primitives/NxpCard.vue";
import NxpActionGroup from "../../ui/composites/NxpActionGroup.vue";
import NotificationEditor from "./NotificationEditor.vue";
import { loadNotificationsPage } from "./first-screen";
import type { NotificationTask } from "./notificationTypes";
const initial = takePreparedData<NotificationTask[]>("notifications");
const tasks = ref<NotificationTask[]>(initial || []);
const error = ref("");
const busy = ref(false);
const editorOpen = ref(false);
const choosingType = ref(false);
const editing = ref<NotificationTask | null>(null);
const deleting = ref<NotificationTask | null>(null);
const type = ref<"smtp" | "webhook">("smtp");
const controller = new AbortController();
async function refresh() { tasks.value = await loadNotificationsPage(controller.signal); }
async function action(operation: () => Promise<unknown>) {
  if (busy.value) return;
  busy.value = true; error.value = "";
  try { await operation(); await refresh(); }
  catch (reason) { if (!isAbortError(reason)) error.value = reason instanceof Error ? reason.message : String(reason); }
  finally { busy.value = false; }
}
function create(value: "smtp" | "webhook") { type.value = value; editing.value = null; choosingType.value = false; editorOpen.value = true; }
function edit(task: NotificationTask) { editing.value = task; type.value = task.type; editorOpen.value = true; }
function saved() { editorOpen.value = false; void action(async () => {}); }
async function remove() {
  const task = deleting.value; if (!task) return;
  await action(async () => { await api("DELETE", `/api/notifications/${task.id}`, undefined, controller.signal); deleting.value = null; });
}
onMounted(() => { setTopbarTitle(t("notifications.title")); if (!initial) void action(async () => {}); });
onBeforeUnmount(() => controller.abort());
</script>
<template>
  <main id="view" class="view-root workspace-page page-notifications" data-testid="main-view">
  <NxpPageHeader :eyebrow="t('notifications.management')" :title="t('notifications.title')" :description="t('notifications.description')">
    <template #actions><NxpButton class="primary" :disabled="busy" @click="choosingType = true">{{ t('notifications.add') }}</NxpButton></template>
  </NxpPageHeader>
  <p v-if="error" role="alert" class="error-text">{{ error }}</p>
  <NxpEmptyState v-if="!tasks.length && !error" :title="t('notifications.empty')" :description="t('notifications.empty_help')" />
  <NxpCard v-else-if="tasks.length" class="list-surface">
  <NxpSortableList class="script-grid" :disabled="busy" @reorder="ids => action(() => api('PUT', '/api/notifications/order', { ids }, controller.signal))">
    <NxpCard v-for="task in tasks" :key="task.id" as="article" unstyled :data-dnd-id="task.id" class="script-card global-user-card">
      <NxpDragHandle class="global-user-drag-handle" :label="t('common.drag_to_reorder')" :disabled="busy" />
      <span class="global-user-avatar-button" aria-hidden="true"><span class="global-user-avatar">{{ Array.from(task.name)[0]?.toLocaleUpperCase() }}</span></span>
      <div class="script-main global-user-main">
        <div class="script-name-row"><strong class="global-user-name"><NxpOverflowText :label="task.name" /></strong></div>
        <div class="meta-line global-user-meta"><NxpBadge tone="muted">{{ task.type === 'smtp' ? 'SMTP' : 'Webhook' }}</NxpBadge><NxpBadge :tone="task.enabled ? 'ok' : 'muted'">{{ t(task.enabled ? 'common.enabled_status' : 'common.disabled') }}</NxpBadge><NxpBadge v-if="!task.configured" tone="warn">{{ t('notifications.incomplete') }}</NxpBadge></div>
      </div>
      <NxpActionGroup class="script-ops row-actions entity-actions"><NxpButton class="tertiary" :disabled="busy" @click="edit(task)">{{ t('notifications.edit') }}</NxpButton><NxpButton tone="danger" :disabled="busy" @click="deleting = task">{{ t('common.delete') }}</NxpButton></NxpActionGroup>
    </NxpCard>
  </NxpSortableList>
  </NxpCard>
  <NxpModal :open="choosingType" :title="t('notifications.add')" @close="choosingType = false">
    <div class="notification-type-choices">
      <NxpButton aria-label="SMTP" @click="create('smtp')"><strong>SMTP</strong><span class="muted">{{ t('notifications.smtp_description') }}</span></NxpButton>
      <NxpButton aria-label="Webhook" @click="create('webhook')"><strong>Webhook</strong><span class="muted">{{ t('notifications.webhook_description') }}</span></NxpButton>
    </div>
  </NxpModal>
  <NotificationEditor v-if="editorOpen" :open="editorOpen" :task="editing" :type="type" @close="editorOpen = false" @saved="saved" />
  <NxpModal :open="!!deleting" :title="t('common.delete')" :locked="busy" @close="deleting = null"><p>{{ t('notifications.delete_confirm', { name: deleting?.name || '' }) }}</p><template #footer><NxpButton class="ghost" :disabled="busy" @click="deleting = null">{{ t('common.cancel') }}</NxpButton><NxpButton class="danger solid" :disabled="busy" @click="remove">{{ t('common.confirm_deletion') }}</NxpButton></template></NxpModal>
  </main>
</template>
<style scoped>
.notification-type-choices { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-4); }
.notification-type-choices button { display: flex; flex-direction: column; align-items: flex-start; justify-content: flex-start; gap: var(--space-2); padding: var(--space-4); text-align: left; white-space: normal; }
.notification-type-choices strong { font-size: 15px; }
.notification-type-choices .muted { font-size: 12px; line-height: 1.55; }
@media (max-width: 560px) { .notification-type-choices { grid-template-columns: minmax(0, 1fr); } }
</style>
