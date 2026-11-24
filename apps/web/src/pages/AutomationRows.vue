<script setup lang="ts">
import { computed } from 'vue';
import type { AutomationEntity, ScriptDraft, UserDraft } from '../app/automation-draft';
import { extensionIcon as scriptIcon } from '../control/extension-icons';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle } from '../control/wire.generated';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import AccountAvatar from './AccountAvatar.vue';
import { locale, t } from '../app/locale';
import { completionLabels } from './execution-plan';
import type { AccountSummary } from '../control/account-summary';
import type { ExtensionState } from '../control/extensions';
import { metadataText } from '../control/extension-metadata';
type Row = AutomationEntity;
type Badge = { label: string; tone: string };
const props = defineProps<{
  kind: 'scripts' | 'users' | 'queues';
  rows: Row[];
  bundle: AutomationBundle;
  scriptCatalog?: readonly ExtensionState[];
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
const avatars = computed(() => {
  const scripts = new Map(props.bundle.scripts.map(script => [script.id, script]));
  return new Map(props.rows.map(row => {
    const script = 'scriptIds' in row ? scripts.get(row.scriptIds[0] ?? '') : 'judgement' in row ? row : undefined;
    return [row.id, script && scriptIcon(script.extensionId)];
  }));
});
const scriptExtensions = computed(() => new Map(props.scriptCatalog?.map(item => [item.extensionId, item])));
function scriptTypeBadge(script: ScriptDraft): Badge {
  if (script.kind === 'generic') return { label: t('通用脚本'), tone: 'neutral' };
  if (!props.scriptCatalog) return { label: t('专项脚本'), tone: 'neutral' };
  const extension = script.extensionId && scriptExtensions.value.get(script.extensionId);
  if (!extension || !extension.isInstalled || !extension.desiredEnabled || !extension.activationId || extension.unavailableReason)
    return { label: t('未知专项'), tone: 'danger' };
  const name = extension.metadata ? metadataText(extension.metadata, locale.value).displayName : extension.artifactName || extension.extensionId;
  return { label: extension.kind === 'managed-code' ? name : `${name} ${t('专项')}`, tone: 'neutral' };
}
function scriptBadges(script: ScriptDraft): Badge[] {
  const result = [scriptTypeBadge(script)];
  if (script.target?.launchByHost)
    result.push({ label: t(script.target.kind === 'android' ? '安卓模拟器' : 'PC 客户端'), tone: 'neutral' });
  const { judgement } = script;
  if (judgement.language !== 'keywords' && judgement.script?.trim()) result.push({ label: t('判定脚本'), tone: 'neutral' });
  else if (judgement.language === 'keywords' && (judgement.successKeywords.length || judgement.failureKeywords.length))
    result.push({ label: t('关键字判断'), tone: 'neutral' });
  if (script.limits.stallTimeoutSeconds === 0) result.push({ label: t('长时策略'), tone: 'warning' });
  return result;
}
function badges(row: Row): Badge[] {
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
  if ('judgement' in row) return scriptBadges(row);
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
      <span v-else class="script-avatar">
        <img v-if="avatars.get(row.id)" :src="avatars.get(row.id)" alt="" />
        <nexus-icon v-else aria-hidden="true" name="queues" />
      </span>
      <div class="row-main">
        <h2 class="entity-card-name">{{ row.name }}</h2>
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
      <div class="entity-card-actions">
        <nexus-icon-button :icon="kind === 'users' ? 'user' : 'edit'" :disabled.prop="locked"
          :label="t(kind === 'users' ? '用户管理' : kind === 'scripts' ? '编辑脚本' : '编辑队列')" @click="emit('edit', row)" />
        <nexus-icon-button v-if="kind === 'users'" icon="settings" :label="t('全局管理')"
          :disabled.prop="locked" @click="emit('global', row)" />
        <nexus-icon-button icon="trash" class="danger-action"
          :label="t(kind === 'users' ? '删除用户' : kind === 'scripts' ? '删除脚本' : '删除队列')"
          :disabled.prop="locked || !store.can('automation.delete')"
          @click="emit('remove', row)"
        />
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
  grid-template-columns: minmax(0, 1fr);
  gap: 8px;
}
.scripts .row-main,
.queues .row-main {
  gap: 6px;
}
.summary-link { border: 0; background: transparent; padding: 0; min-height: 0; min-width: 0; max-width: 100%; }
.row-meta {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
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
@media (max-width: 600px) {
  .automation-row { gap: 6px; padding-inline: 8px; }
}
</style>
