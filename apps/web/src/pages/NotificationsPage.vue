<script setup lang="ts">
import { createIdentity } from '../control/identity';
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { ControlFailure, objectValue } from '../control/client';
import type { ControlNotificationCommand } from '../control/wire.generated';
import NotificationEditor from './NotificationEditor.vue';
import { notificationEditorPort } from './notification-commands';
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
const editorPort = computed(() =>
  notificationEditorPort(props.store, instanceId, editing.value?.id || 'new/' + channel.value)
);
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
    if (!(reason instanceof ControlFailure && reason.uncertain)) {
      session.error = reason instanceof Error ? reason.message : '操作失败';
      session.pending = null;
    }
    return false;
  } finally {
    session.busy = false;
  }
}
async function checkPending(): Promise<void> {
  const body = session.pending;
  if (!isCurrent() || !body || session.busy || !props.store.can('operations.read')) return;
  session.busy = true;
  session.error = session.notice = '';
  try {
    const result = await props.store.client.read('/control/v1/operations/' + body.operationId, objectValue);
    const kind = body.action === 'delete' ? 'notification.task.delete' : 'notification.catalog.reorder';
    if (result.operationId !== body.operationId || result.kind !== kind) throw Error('通知操作类型不匹配。');
    if (result.status === 'completed') {
      session.pending = null;
      session.error = '';
      session.notice = '原通知操作已确认完成。';
      await load();
    } else session.notice = '原通知操作仍在处理，请稍后查询。';
  } catch {
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
    <ModalTransition v-if="choosing">
      <nexus-modal :open.prop="visible" :title="t('添加通知任务')" @nexus-close="choosing = false">
        <div class="channel-choices">
          <button aria-label="SMTP" @click="openNew('smtp')">
            <strong>SMTP</strong>
            <span>{{ t('通过邮件服务器发送通知。') }}</span>
          </button>
          <button aria-label="Webhook" @click="openNew('feishu')">
            <strong>Webhook</strong>
            <span>{{ t('发送到飞书、企业微信、Slack、Discord 或自定义 Webhook。') }}</span>
          </button>
        </div>
      </nexus-modal>
    </ModalTransition>
    <template v-if="visible && !editor">
      <nexus-toast v-if="session.error" :tone="session.pending ? 'warning' : 'danger'" :message.prop="t(session.error)" />
      <nexus-toast v-else-if="session.notice" :message.prop="t(session.notice)" @nexus-close="session.notice = ''" />
      <nexus-toast v-else-if="session.pending && !session.busy" tone="warning" :message.prop="t('写入结果待确认，请先核实原操作。')" />
    </template>
    <nexus-spinner v-if="loading && !catalog" :label="t('通知任务加载中…')" />
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
          <h2 class="entity-card-name">{{ task.name }}</h2>
          <div class="badges">
            <nexus-badge :label="task.kind === 'smtp' ? 'SMTP' : 'Webhook'" />
            <nexus-badge
              :label="t(task.isEnabled ? '已启用' : '已停用')"
              :tone="task.isEnabled ? 'success' : 'neutral'"
            />
          </div>
        </div>
        <div class="entity-card-actions">
          <nexus-icon-button icon="edit" :label="t('编辑通知')"
            :disabled.prop="session.busy || !!session.pending || !store.can('notifications.write')"
            @click="openEdit(task)"
          />
          <nexus-icon-button icon="trash" class="danger-action" :label="t('删除')"
            :disabled.prop="session.busy || !!session.pending || !store.can('notifications.delete')"
            @click="deleting = task"
          />
        </div>
      </article>
    </nexus-sortable-list>
    <nexus-empty-state
      v-else-if="catalog"
      :title="t('尚未添加通知任务')"
      :description="t('点击右上角“添加通知任务”，配置 SMTP 或 Webhook 发送目标。')"
    />
    <ModalTransition>
      <NotificationEditor
        v-if="editor && catalog"
        :key="editing?.id || channel"
        :port="editorPort"
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
        :message="t('删除「{name}」会清除其发送凭据。引用它的配置将无法再通过该任务发送通知。').replace('{name}', () => deleting!.name)"
        tone="danger"
        @nexus-confirm="remove"
        @nexus-cancel="deleting = null"
        @nexus-close="deleting = null"
      />
    </ModalTransition>
  </main>
</template>

<style scoped>
.notifications-page {
  display: grid;
  gap: 16px;
  min-width: 0;
}
.task-list {
  display: grid;
  gap: 14px;
}
.task {
  border: 1px solid var(--border);
  border-radius: 16px;
  background: var(--panel);
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 80px;
  padding: 12px;
}
.avatar {
  display: grid;
  place-items: center;
  flex: none;
  width: 36px;
  height: 36px;
  border: 1px solid var(--border);
  border-radius: 5px;
  background: var(--accent-soft);
  color: var(--accent);
}
.copy {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 6px;
  flex: 1;
  min-width: 0;
  padding-left: 4px;
}
.badges {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}
@media (max-width: 600px) {
  .task { gap: 6px; padding-inline: 8px; }
}

.channel-choices {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
}
.channel-choices > button {
  display: grid;
  align-content: start;
  gap: 6px;
  min-height: 88px;
  padding: 16px;
  text-align: left;
  background: transparent;
}
.channel-choices > button:hover {
  border-color: var(--accent);
  background: var(--accent-soft);
}
.channel-choices strong {
  font-size: 15px;
}
.channel-choices span {
  font-size: 12px;
  color: var(--muted);
  font-weight: 400;
}
@media (max-width: 600px) {
  .channel-choices {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
