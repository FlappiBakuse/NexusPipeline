<script setup lang="ts">
import { computed } from 'vue';
import type { AutomationEntity, UserDraft } from '../app/automation-draft';
import { scriptIcon } from './script-icons';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle } from '../control/wire.generated';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import AccountAvatar from './AccountAvatar.vue';
import { t } from '../app/locale';
import { completionLabels } from './execution-plan';
import type { AccountSummary } from '../control/account-summary';
type Row = AutomationEntity;
const props = defineProps<{
  kind: 'scripts' | 'users' | 'queues';
  rows: Row[];
  bundle: AutomationBundle;
  store: AutomationStore;
  visible: boolean;
  locked: boolean;
  summaries: ReadonlyMap<string, AccountSummary>;
  summaryNow: number;
}>();
const emit = defineEmits<{
  edit: [row: Row];
  global: [row: Row];
  remove: [row: Row];
  reorder: [ids: string[]];
  avatar: [user: AutomationBundle['users'][number], revision: number, catalogRevision: number];
  error: [message: string];
}>();
const bindingCounts = computed(() => {
  const counts = new Map<string, number>();
  for (const binding of props.bundle.bindings) counts.set(binding.userId, (counts.get(binding.userId) || 0) + 1);
  return counts;
});
const revisions = computed(() => new Map((props.store.state.automation?.revisions || []).filter(item => item.kind === 'user').map(item => [item.id, item.revision])));
const isUser = (row: Row): row is UserDraft => !('judgement' in row) && !('scriptIds' in row);
const extensionId = (row: Row): string | undefined => 'extensionId' in row ? row.extensionId : undefined;
function badges(row: Row): { label: string; tone: string }[] {
  if (props.kind === 'users')
    return [
      {
        label:
          t('已绑定') +
          ' ' +
          (bindingCounts.value.get(row.id) || 0) +
          ' ' +
          t('个脚本'),
        tone: 'neutral'
      },
      { label: nextLabel(props.summaries.get(row.id)), tone: 'info' }
    ];
  if ('judgement' in row) {
    const judgement = row.judgement;
    const result = [{ label: t(row.kind === 'specialized' ? '专项脚本' : '通用脚本'), tone: 'neutral' }];
    if (judgement?.language !== 'keywords') result.push({ label: t('判定脚本'), tone: 'neutral' });
    else if (judgement.successKeywords.length || judgement.failureKeywords.length)
      result.push({ label: t('关键字判断'), tone: 'neutral' });
    return result;
  }
  if (!('scriptIds' in row)) return [];
  return [
    { label: row.scriptIds.length + ' ' + t('个任务'), tone: 'neutral' },
    {
      label:
        t(row.completionAction === 'none' ? '完成后无操作' : '完成后') +
        (row.completionAction === 'none' ? '' : t(completionLabels[row.completionAction as keyof typeof completionLabels])),
      tone: 'neutral'
    },
    {
      label: t(
        row.runOnStartup
          ? '启动时运行'
          : (props.bundle.schedules.some((item) => item.queueId === row.id && item.enabled))
            ? '定时运行'
            : '不自动运行'
      ),
      tone: 'info'
    },
    {
      label: t(row.notifyEnabled ? '队列通知已开启' : '队列通知未开启'),
      tone: row.notifyEnabled ? 'success' : 'neutral'
    }
  ];
}
const summaryLabels = { no_bindings: '未配置专项任务', not_run: '尚未运行', record_deleted: '记录已删除', no_task_report: '无任务报告', latest: '最近任务' };
const tones = { bad: 'danger', warn: 'warning', muted: 'neutral', ok: 'success' };
function nextLabel(item?: AccountSummary): string {
  if (!item) return t('定时任务不可读取');
  if (!item.nextRunAt) return t('暂无定时任务');
  const seconds = Math.max(0, Math.ceil((Date.parse(item.nextRunAt) - props.summaryNow) / 1000));
  return t('下次运行') + ' · ' + (seconds >= 86400 ? Math.floor(seconds / 86400) + t('天') + ' ' : '') +
    [Math.floor(seconds / 3600) % 24, Math.floor(seconds / 60) % 60, seconds % 60].map(value => String(value).padStart(2, '0')).join(':');
}
async function openSummary(row: Row): Promise<void> {
  const run = props.summaries.get(row.id)?.runId;
  if (!run) { emit('edit', row); return; }
  await props.store.navigate('history');
  if (props.store.state.page === 'history') location.hash = '#/history?run=' + encodeURIComponent(run) + '&user=' + encodeURIComponent(row.id);
}
</script>

<template>
  <nexus-sortable-list
    class="automation-rows"
    :class="kind"
    :ids.prop="rows.map((item) => item.id)"
    :disabled.prop="locked"
    @nexus-reorder="emit('reorder', $event.detail.ids)"
  >
    <article v-for="row in rows" :key="row.id" :data-item-id="row.id" class="automation-row">
      <nexus-drag-handle :item-id="row.id" :label="t('拖动或使用方向键排序')" :disabled.prop="locked" />
      <AccountAvatar
        v-if="kind === 'users' && isUser(row)"
        :user="row"
        :disabled="locked || !store.can('automation.write')"
        :revision="revisions.get(row.id) || 0"
        :catalog-revision="store.state.automation?.catalogRevision || 0"
        @change="(user, revision, catalogRevision) => emit('avatar', user, revision, catalogRevision)"
        @error="emit('error', $event)"
      />
      <span v-else class="script-avatar"><img v-if="kind === 'scripts'" :src="scriptIcon(extensionId(row))" alt="" /><nexus-icon v-else aria-hidden="true" name="queues" /></span>
      <div class="row-main">
        <button class="entity-link" :disabled="locked" @click="emit('edit', row)">{{ row.name }}</button>
        <div class="row-meta">
          <button v-if="kind === 'users'" class="summary-link" :disabled="locked" @click="openSummary(row)">
            <nexus-badge :label="t('专项任务') + ' · ' + t(summaries.has(row.id) ? summaryLabels[summaries.get(row.id)!.summaryReason] : '不可读取')"
              :tone="tones[summaries.get(row.id)?.summaryTone || 'muted']" />
          </button>
          <nexus-badge v-if="kind === 'users' && summaries.get(row.id)?.activeCount" tone="info"
            :label="t('运行中') + ' · ' + summaries.get(row.id)!.activeCount" />
          <nexus-badge
            v-for="(badge, index) in badges(row)"
            :key="index"
            :label="badge.label"
            :tone="badge.tone"
          />
          <ExtensionOutlet
            :target="
              kind === 'users'
                ? 'automation.users.badges'
                : kind === 'scripts'
                  ? 'automation.scripts.badges'
                  : 'automation.queues.badges'
            "
            :store="store"
            :visible="visible"
            :entity-id="row.id"
          />
        </div>
      </div>
      <div class="row-actions">
        <button :disabled="locked" @click="emit('edit', row)">
          {{ t(kind === 'users' ? '用户管理' : kind === 'scripts' ? '编辑脚本' : '编辑队列') }}
        </button>
        <button v-if="kind === 'users'" :disabled="locked" @click="emit('global', row)">
          {{ t('全局管理') }}
        </button>
        <button
          class="danger"
          :disabled="locked || !store.can('automation.delete')"
          @click="emit('remove', row)"
        >
          {{ t(kind === 'users' ? '删除用户' : kind === 'scripts' ? '删除脚本' : '删除队列') }}
        </button>
      </div>
    </article>
  </nexus-sortable-list>
</template>

<style scoped>
.automation-rows {
  display: grid;
  gap: 14px;
}
.automation-row {
  border: 1px solid var(--border);
  border-radius: 16px;
  background: var(--panel);
  display: flex;
  min-height: 80px;
  gap: 8px;
  align-items: center;
  padding: 12px;
}
nexus-drag-handle {
  flex: none;
  width: 24px;
}
.row-main {
  min-width: 0;
  flex: 1;
  display: grid;
  gap: 8px;
}
.scripts .row-main,
.queues .row-main {
  gap: 6px;
}
.entity-link {
  display: block;
  justify-self: start;
  padding: 0;
  min-height: 18px;
  max-width: 100%;
  border: 0;
  background: transparent;
  text-align: left;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.entity-link:hover {
  color: var(--accent);
}
.summary-link { border: 0; background: transparent; padding: 0; min-height: 0; }
.scripts .entity-link,
.queues .entity-link {
  font-size: 14px;
  font-weight: 400;
  line-height: 1.55;
}
.row-meta {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}
.row-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  justify-content: flex-end;
}
.row-actions > button {
  min-width: 96px;
  color: var(--muted);
}
.row-actions > .danger {
  color: var(--bad);
  background: var(--bad-soft, #ef707020);
  border-color: var(--bad-border, #ef707080);
}
.script-avatar {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  min-height: 36px;
  flex: none;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--content-control);
  color: var(--accent);
}
.script-avatar img { width: 36px; height: 36px; object-fit: contain; }
.script-avatar nexus-icon {
  color: var(--bg);
}
@media (max-width: 900px) {
  .automation-row {
    flex-wrap: wrap;
  }
  .row-main {
    flex-basis: calc(100% - 88px);
  }
  .row-actions {
    width: 100%;
    flex-wrap: nowrap;
  }
  .row-actions > button:first-child { flex: 1; }
  .row-actions > button { min-width: 0; }
}
</style>
