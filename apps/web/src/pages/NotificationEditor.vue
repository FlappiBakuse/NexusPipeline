<script setup lang="ts">
import { createIdentity } from '../control/identity';
import { computed, onBeforeUnmount, reactive, ref, toRefs, watch } from 'vue';
import type { AppStore } from '../app/store';
import NotificationFields from './NotificationFields.vue';
import { NotificationCredentials } from './notification-credentials';
import { NotificationCommands, type NotificationSession } from './notification-commands';
import { validateForm } from '../components/form-validation';
import { t } from '../app/locale';
import { copyDraft } from '../app/draft-copy';
import './notification-messages';
import {
  newNotification,
  notificationCatalog,
  type NotificationChannel,
  type NotificationTask,
  type SecretField
} from './notification-types';

const props = defineProps<{
  store: AppStore;
  task: NotificationTask | null;
  channel: NotificationChannel;
  catalogRevision: number;
}>();

const emit = defineEmits<{ close: []; saved: [] }>();
const panel = ref<HTMLElement>();
const instanceId = props.store.state.capabilities!.instanceId;
const isCurrent = (): boolean => props.store.state.capabilities?.instanceId === instanceId;
const draftKey = 'notification/editor/' + instanceId + '/' + (props.task?.id || 'new/' + props.channel);
const savedDraft = props.store.drafts.get(draftKey);
const initial = props.task
  ? {
      name: props.task.name,
      remark: props.task.remark,
      kind: props.task.kind,
      isEnabled: props.task.isEnabled,
      configuration: copyDraft(props.task.configuration)
    }
  : newNotification(props.channel);
const session = reactive(
  savedDraft && typeof savedDraft.baseline === 'string'
    ? savedDraft
    : {
        draft: initial,
        baseline: JSON.stringify(initial),
        catalogRevision: props.catalogRevision,
        taskRevision: props.task?.revision ?? 0,
        taskId: props.task?.id || createIdentity(),
        pending: '',
        pendingAction: '',
        busy: false,
        error: '',
        notice: '',
        completed: false,
        conflict: false
      }
) as NotificationSession;
props.store.drafts.set(draftKey, session);
const { draft, baseline, pending, busy, error, notice } = toRefs(session);
let alive = true;
const credentials = new NotificationCredentials(
  props.store,
  props.task?.id,
  () => draft.value,
  () => session.catalogRevision,
  (message) => {
    error.value = message;
  }
);
const commands = new NotificationCommands(props.store, session, credentials, draftKey, !!props.task, () =>
  validateForm(panel.value)
);
const credentialsReady = computed(() => credentials.view.ready);
const values = computed(() => credentials.view.values);
const configured = computed(() =>
  Object.fromEntries(credentials.view.statuses.map((item) => [item.field, item.isConfigured]))
);
const dirty = computed(() => JSON.stringify(draft.value) !== baseline.value || credentials.dirty);
const loadCredentials = (id: string) => credentials.read(id);
const editSecret = (field: SecretField, value: string) => credentials.edit(field, value);
watch(
  () => credentials.shown,
  (fields) => credentials.retain(fields),
  { flush: 'sync' }
);
watch(
  [dirty, pending],
  ([edited, operation]) => {
    if (edited || operation) props.store.dirty.add(draftKey);
    else props.store.dirty.delete(draftKey);
  },
  { immediate: true, flush: 'sync' }
);
if (props.task) void loadCredentials(props.task.id);
onBeforeUnmount(() => {
  alive = false;
  credentials.dispose();
  if (!pending.value && JSON.stringify(draft.value) === baseline.value) props.store.dirty.delete(draftKey);
});

watch(
  () => session.completed,
  (completed) => {
    if (completed && alive && isCurrent()) emit('saved');
  },
  { flush: 'post' }
);
async function loadLatest(): Promise<void> {
  if (!isCurrent() || pending.value || busy.value) return;
  busy.value = true;
  try {
    const latest = await props.store.client.read('/control/v1/notifications', notificationCatalog);
    if (!isCurrent()) return;
    const task = latest.tasks.find((item) => item.id === session.taskId);
    if (props.task && !task) {
      error.value = '通知任务已删除，当前草稿已保留。';
      return;
    }
    if (task) {
      draft.value = {
        name: task.name,
        remark: task.remark,
        kind: task.kind,
        isEnabled: task.isEnabled,
        configuration: copyDraft(task.configuration)
      };
      baseline.value = JSON.stringify(draft.value);
      session.taskRevision = task.revision;
    }
    if (props.store.state.error === error.value) props.store.state.error = '';
    session.catalogRevision = latest.revision;
    session.conflict = false;
    error.value = '';
    credentials.clear();
    if (task) await loadCredentials(task.id);
  } catch (reason) {
    props.store.report(reason);
    error.value = '无法读取最新通知任务，草稿已保留。';
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <nexus-modal
    :open.prop="true"
    :title="t(task ? '编辑通知' : '添加通知任务')"
    size="wide"
    :locked.prop="busy"
    @nexus-close="emit('close')"
  >
    <div ref="panel" class="editor">
      <NotificationFields
        v-model="draft"
        :disabled="busy || !!pending"
        :credentials-ready="credentialsReady"
        :secrets="values"
        :configured="configured"
        @secret="editSecret"
      />
      <p v-if="error" class="error" role="alert">
        {{ t(error) }}
        <button v-if="session.conflict" :disabled="busy || !!pending" @click="loadLatest">
          {{ t('载入最新') }}
        </button>
        <button v-if="task && !credentialsReady" :disabled="busy" @click="loadCredentials(task.id)">
          {{ t('重试') }}
        </button>
      </p>
      <p v-if="notice" role="status">{{ t(notice) }}</p>
      <p v-if="pending" role="alert">
        {{ t('写入结果待确认，请先核实原操作。') }} {{ pending }}
        <button :disabled="busy || !store.can('operations.read')" @click="commands.checkPending()">
          {{ t('查询操作状态') }}
        </button>
      </p>
    </div>
    <div slot="footer" class="actions">
      <button
        v-if="task"
        :disabled="busy || !!pending || dirty || !store.can('notifications.test')"
        :title="dirty ? t('先保存当前修改，再测试已保存的任务。') : ''"
        @click="commands.test()"
      >
        {{ t('测试通知') }}
      </button>
      <button :disabled="busy" @click="emit('close')">{{ t('取消') }}</button>
      <button
        class="primary"
        :disabled="
          busy || !!pending || session.conflict || !credentialsReady || !store.can('notifications.write')
        "
        @click="commands.save()"
      >
        {{ t(busy ? '保存中…' : '保存') }}
      </button>
    </div>
  </nexus-modal>
</template>

<style scoped src="./notification-editor.css"></style>
