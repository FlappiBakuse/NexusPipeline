<script setup lang="ts">
import { createIdentity } from '../control/identity';
import { onBeforeUnmount, reactive, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { ControlFailure, objectValue } from '../control/client';
import type { ControlNotificationCommand } from '../control/wire.generated';
import NotificationEditor from './NotificationEditor.vue';
import NotificationChannelChooser from './NotificationChannelChooser.vue';
import ModalTransition from '../components/ModalTransition.vue';
import { copyDraft } from '../app/draft-copy';
import { t } from '../app/locale';
import './notification-messages';
import {
  notificationCatalog,
  type NotificationCatalog,
  type NotificationPageSession,
  type NotificationChannel,
  type NotificationTask
} from './notification-types';

const props = defineProps<{ store: AppStore; visible: boolean }>();
const catalog = ref<NotificationCatalog | null>(null);
const loading = ref(false);
const editor = ref(false);
const choosing = ref(false);
const editing = ref<NotificationTask | null>(null);
const channel = ref<NotificationChannel>('smtp');
const deleting = ref<NotificationTask | null>(null);
const instanceId = props.store.state.capabilities!.instanceId;
const sessionKey = 'notification/page/' + instanceId;
const session = reactive(
  props.store.drafts.get(sessionKey) || {
    pending: null,
    busy: false,
    error: '',
    notice: ''
  }
) as unknown as NotificationPageSession;
props.store.drafts.set(sessionKey, session as unknown as Record<string, unknown>);
let readOwner: AbortController | undefined;
const isCurrent = (): boolean => props.store.state.capabilities?.instanceId === instanceId;
watch(
  () => session.pending,
  (pending) => {
    if (pending) props.store.dirty.add(sessionKey);
    else props.store.dirty.delete(sessionKey);
  },
  { immediate: true, flush: 'sync' }
);
watch(
  () => props.visible,
  (visible) => {
    if (visible && !editor.value) void load();
    else readOwner?.abort();
    if (!visible) {
      editor.value = false;
      choosing.value = false;
      deleting.value = null;
    }
  },
  { immediate: true }
);
onBeforeUnmount(() => readOwner?.abort());
watch(() => props.store.state.preparationRevision, () => {
  if (props.visible && !editor.value && !session.pending) void load();
});
watch(editor, (open, previous) => {
  if (!open && previous && props.visible && !session.pending) void load();
});

async function load(): Promise<void> {
  readOwner?.abort();
  if (!props.visible || !isCurrent()) return;
  const owner = new AbortController();
  readOwner = owner;
  loading.value = true;
  try {
    const result = await props.store.client.read(
      '/control/v1/notifications',
      notificationCatalog,
      owner.signal
    );
    owner.signal.throwIfAborted();
    if (!isCurrent()) return;
    catalog.value = result;
  } catch (reason) {
    if (!owner.signal.aborted && isCurrent()) {
      props.store.report(reason);
      session.error = reason instanceof Error ? reason.message : '通知任务加载失败';
    }
  } finally {
    if (readOwner === owner) loading.value = false;
  }
}
function openNew(value: NotificationChannel): void {
  choosing.value = false;
  channel.value = value;
  editing.value = null;
  editor.value = true;
}
function openEdit(task: NotificationTask): void {
  channel.value = task.kind === 'smtp' ? 'smtp' : task.configuration.webhookKind;
  editing.value = task;
  editor.value = true;
}
async function command(
  action: 'delete' | 'reorder',
  fields: Partial<ControlNotificationCommand>
): Promise<boolean> {
  const capability = action === 'delete' ? 'notifications.delete' : 'notifications.write';
  if (!isCurrent() || !catalog.value || session.busy || session.pending || !props.store.can(capability))
    return false;
  const body: ControlNotificationCommand = {
    format: 'nexus.control.notification-command',
    version: 1,
    action,
    operationId: createIdentity(),
    expectedCatalogRevision: catalog.value.revision,
    ...fields
  };
  session.pending = copyDraft(body);
  session.busy = true;
  session.error = '';
  session.notice = '';
  try {
    const saved = await props.store.client.write(
      '/control/v1/notifications/' + action,
      body,
      (value) => {
        const result = notificationCatalog(value);
        if (result.revision !== body.expectedCatalogRevision + 1)
          throw new TypeError('notification_result_unbound');
        if (action === 'delete' && result.tasks.some((task) => task.id === body.taskId))
          throw new TypeError('notification_delete_unbound');
        return result;
      },
      body.operationId
    );
    session.pending = null;
    if (isCurrent()) catalog.value = saved;
    session.notice = '通知任务已更新。';
    return true;
  } catch (reason) {
    props.store.report(reason);
    session.error = reason instanceof Error ? reason.message : '操作失败';
    if (!(reason instanceof ControlFailure && reason.uncertain)) session.pending = null;
    return false;
  } finally {
    session.busy = false;
  }
}
async function checkPending(): Promise<void> {
  const body = session.pending;
  if (!isCurrent() || !body || session.busy || !props.store.can('operations.read')) return;
  session.busy = true;
  try {
    const result = await props.store.client.read('/control/v1/operations/' + body.operationId, objectValue);
    const kind = body.action === 'delete' ? 'notification.task.delete' : 'notification.catalog.reorder';
    if (result.operationId !== body.operationId || result.kind !== kind) throw Error('通知操作类型不匹配。');
    if (result.status === 'completed') {
      session.pending = null;
      session.error = '';
      session.notice = '原通知操作已确认完成。';
      if (props.store.state.uncertainOperation === body.operationId) {
        props.store.state.uncertainOperation = '';
        props.store.state.error = '';
      }
      await load();
    } else session.notice = '原通知操作仍在处理，请稍后查询。';
  } catch (reason) {
    props.store.report(reason);
    session.error = '无法确认原通知操作；操作 ID 已保留，勿重复发送。';
  } finally {
    session.busy = false;
  }
}
async function remove(): Promise<void> {
  const task = deleting.value;
  if (!task) return;
  deleting.value = null;
  await command('delete', { taskId: task.id, expectedTaskRevision: task.revision });
}
async function reorder(ids: string[]): Promise<void> {
  if (
    !catalog.value ||
    ids.length !== catalog.value.tasks.length ||
    new Set(ids).size !== ids.length ||
    ids.some((id) => !catalog.value!.tasks.some((task) => task.id === id))
  )
    return;
  await command('reorder', { order: ids });
}
</script>

<template>
  <main class="notifications-page">
    <PageActions :visible="visible">
      <button
        class="primary"
        :disabled="
          session.busy ||
          !!session.pending ||
          !store.can('notifications.write') ||
          (!!catalog && catalog.tasks.length >= catalog.maximumTasks)
        "
        @click="choosing = true"
      >
        {{ t('添加通知任务') }}
      </button>
        <button v-if="session.pending" :disabled="session.busy || !store.can('operations.read')" @click="checkPending">{{ t('查询操作状态') }}</button>
  </PageActions>
    <NotificationChannelChooser
      v-if="choosing"
      :visible="visible"
      @select="openNew"
      @close="choosing = false"
    />
    <nexus-toast v-if="session.error" tone="danger" :message.prop="t(session.error)" />
    <nexus-toast v-if="session.notice" :message.prop="t(session.notice)" @nexus-close="session.notice = ''" />
    <nexus-toast v-if="session.pending" tone="warning" :message.prop="t('写入结果待确认，请先核实原操作。')" />
    <p v-if="loading && !catalog" role="status">{{ t('通知任务加载中…') }}</p>
    <nexus-sortable-list
      v-else-if="catalog && catalog.tasks.length"
      class="task-list"
      :aria-label="t('通知任务')"
      :ids.prop="catalog.tasks.map((task) => task.id)"
      :disabled.prop="session.busy || !!session.pending || !store.can('notifications.write')"
      @nexus-reorder="reorder($event.detail.ids)"
    >
      <article v-for="task in catalog.tasks" :key="task.id" class="task" :data-item-id="task.id">
        <nexus-drag-handle
          :item-id="task.id"
          :label="t('拖拽排序')"
          :disabled.prop="session.busy || !!session.pending || !store.can('notifications.write')"
        />
        <span class="avatar" aria-hidden="true">{{ task.name.slice(0, 1) }}</span>
        <div class="copy">
          <h2>{{ task.name }}</h2>
          <div class="badges">
            <nexus-badge :label="task.kind === 'smtp' ? 'SMTP' : 'Webhook'" />
            <nexus-badge
              :label="t(task.isEnabled ? '已启用' : '已停用')"
              :tone="task.isEnabled ? 'success' : 'neutral'"
            />
          </div>
        </div>
        <div class="actions">
          <button
            :disabled="session.busy || !!session.pending || !store.can('notifications.write')"
            @click="openEdit(task)"
          >
            {{ t('编辑通知') }}
          </button>
          <button
            class="danger"
            :disabled="session.busy || !!session.pending || !store.can('notifications.delete')"
            @click="deleting = task"
          >
            {{ t('删除') }}
          </button>
        </div>
      </article>
    </nexus-sortable-list>
    <div v-else-if="catalog" class="empty">
      <h2>{{ t('尚未添加通知任务') }}</h2>
      <p>{{ t('点击右上角“添加通知任务”，配置 SMTP 或 Webhook 发送目标。') }}</p>
    </div>
    <ModalTransition>
      <NotificationEditor
        v-if="editor && catalog"
        :key="editing?.id || channel"
        :store="store"
        :task="editing"
        :channel="channel"
        :catalog-revision="catalog.revision"
        @close="editor = false"
        @saved="editor = false"
      />
    </ModalTransition>
    <ModalTransition>
      <nexus-confirm-dialog
        v-if="deleting"
        :open.prop="visible"
        :title="t('删除通知任务')"
        :message="t('确定删除') + ' ' + deleting.name + t('？')"
        tone="danger"
        @nexus-confirm="remove"
        @nexus-cancel="deleting = null"
        @nexus-close="deleting = null"
      />
    </ModalTransition>
  </main>
</template>

<style scoped src="./notifications-page.css"></style>
