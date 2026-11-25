<script setup lang="ts">
import { registerMessages, t } from '../app/locale';
import type { ScriptSource } from '../control/wire.generated';

type Preview = NonNullable<ScriptSource['accountPreview']>;
defineProps<{ preview: Preview }>();

const tasks: Record<Preview['accounts'][number]['task'], string> = {
  DailyTask: '日常任务',
  RahaiBlocksTask: '拉海洛方块',
  FarmEchoTask: '刷取声骸',
  NightmareNestTask: '梦魇巢穴',
  TacetTask: '无音区',
  ForgeryTask: '凝素领域',
  SimulationTask: '模拟领域',
  CultivationTask: '养成任务',
  MergeEchoTask: '声骸合成',
  EnhanceEchoTask: '强化声骸',
  ChangeEchoTask: '更换声骸',
  GardenTask: '每周花园',
  loginOnly: '仅登录',
  unknown: '未知任务'
};
registerMessages({
  '多账号配置（只读）': 'Multi-account configuration (read-only)',
  '尚未保存多账号配置。': 'No multi-account configuration saved.',
  '多账号配置格式无效，请在 OkWW 中检查。': 'The multi-account configuration is invalid. Check it in OkWW.',
  '原生按账号配置已启用': 'Native per-account configuration enabled',
  '原生按账号配置已关闭': 'Native per-account configuration disabled',
  '原生按账号配置状态未知': 'Native per-account configuration status unknown',
  '账号 {number}': 'Account {number}',
  '{count} 项配置覆盖': '{count} configuration overrides',
  '账号已启用': 'Enabled account',
  '账号已禁用': 'Disabled account',
  日常任务: 'Daily tasks',
  拉海洛方块: 'Rahai Blocks',
  刷取声骸: 'Farm Echoes',
  梦魇巢穴: 'Nightmare Nest',
  无音区: 'Tacet Suppression',
  凝素领域: 'Forgery Challenge',
  模拟领域: 'Simulation Challenge',
  养成任务: 'Cultivation',
  声骸合成: 'Merge Echoes',
  强化声骸: 'Enhance Echoes',
  更换声骸: 'Change Echoes',
  每周花园: 'Weekly Garden',
  仅登录: 'Login only',
  未知任务: 'Unknown task'
});
</script>

<template>
  <section class="account-preview">
    <h3>{{ t('多账号配置（只读）') }}</h3>
    <p>{{ t(preview.mode === 'enabled' ? '原生按账号配置已启用'
      : preview.mode === 'disabled' ? '原生按账号配置已关闭' : '原生按账号配置状态未知') }}</p>
    <ol v-if="preview.state === 'ready'" role="list">
      <li v-for="(account, index) in preview.accounts" :key="index">
        <strong>{{ t('账号 {number}').replace('{number}', String(index + 1)) }}</strong>
        <span>{{ t(account.enabled ? '账号已启用' : '账号已禁用') }}</span>
        <span>{{ t(tasks[account.task]) }}</span>
        <span>{{ t('{count} 项配置覆盖').replace('{count}', String(account.overrideCount)) }}</span>
      </li>
    </ol>
    <p v-else>{{ t(preview.state === 'empty' ? '尚未保存多账号配置。' : '多账号配置格式无效，请在 OkWW 中检查。') }}</p>
  </section>
</template>

<style scoped>
.account-preview {
  border-top: 1px solid var(--border);
  padding-top: 12px;
}
.account-preview h3 {
  margin: 0 0 6px;
  font-size: 13px;
}
.account-preview p {
  margin: 0 0 6px;
  color: var(--muted);
}
.account-preview ol {
  margin: 0;
  padding: 0;
  list-style: none;
}
.account-preview li {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  padding: 6px 0;
  color: var(--muted);
}
.account-preview strong {
  color: var(--text);
}
</style>
