import type { ExecutionPreview } from '../control/execution';
import type { AutomationBundle } from '../control/wire.generated';
import { locale, registerMessages, t } from '../app/locale';

export type PlanTargetKind = 'queue' | 'script';

export interface PlanAccount {
  ordinal: number;
  bindingId: string;
  scriptId: string;
  userId: string;
  reasonCode: string;
  state: 'ready' | 'attention' | 'blocked';
  skipped: boolean;
  reason: string;
  successfulRunsToday?: number;
  maximumSuccessfulRunsPerDay?: number;
}

const reasons: Record<string, string> = {
  execution_inputs_verified: '符合当前运行条件',
  execution_capture_runtime_unbound: '截图助手校验失败，请检查 Host 安装文件。',
  execution_binding_disabled: '账号绑定未启用',
  execution_run_days_exhausted: '运行天数已用完',
  execution_daily_success_limit: '今日成功运行次数已达上限',
  execution_extension_disabled: '专项插件未启用',
  configuration_original_lost: '脚本实例配置丢失，请重新编辑或导入。',
  configuration_recovery_pending: '此脚本实例的配置还在恢复。',
  execution_queue_dependency_not_successful: '前置任务未满足成功条件',
  execution_no_enabled_bindings: '没有可用的账号绑定，请启用账号及其脚本实例。',
  execution_script_missing: '脚本实例不存在',
  execution_queue_missing: '调度队列不存在',
  execution_no_runnable_targets: '此计划没有可执行的任务。',
  configuration_required: '此脚本实例尚未保存配置。',
  execution_queue_empty: '此计划没有可执行的任务。'
};

const nextSteps: Record<string, string> = {
  execution_daily_success_limit: '今天无需重复运行，可等到下次计划。',
  execution_extension_disabled: '在插件中心启用对应的专项插件后重新检查。',
  execution_binding_disabled: '在账号与配置中核对该脚本实例的绑定状态。',
  configuration_original_lost: '在账号与配置中重新编辑或导入该绑定的配置。',
  configuration_required: '在账号与配置中完成该绑定的配置。',
  configuration_recovery_pending: '在系统诊断中查看配置恢复状态，恢复后重新检查。'
};

export function planReason(code: string): string {
  return t(reasons[code] || code);
}

export function planAccounts(preview: ExecutionPreview): PlanAccount[] {
  return [...preview.items].sort((left, right) => left.ordinal - right.ordinal).map((item) => {
    const { disposition, successfulRunsToday: successes, maximumSuccessfulRunsPerDay: maximum } = item;
    const reason = planReason(item.reasonCode);
    const count =
      item.reasonCode === 'execution_daily_success_limit' && successes != null && maximum != null
        ? locale.value === 'zh-CN'
          ? `（${successes}/${maximum}）`
          : ` (${successes}/${maximum})`
        : '';
    return {
      ordinal: item.ordinal,
      bindingId: item.bindingId,
      scriptId: item.scriptId,
      userId: item.userId,
      reasonCode: item.reasonCode,
      state: disposition === 'runnable' ? 'ready' : disposition === 'skipped' ? 'attention' : 'blocked',
      skipped: disposition === 'skipped',
      reason: reason + count,
      ...(successes == null ? {} : { successfulRunsToday: successes }),
      ...(maximum == null ? {} : { maximumSuccessfulRunsPerDay: maximum })
    };
  });
}

interface PlanGroup {
  order: number;
  scriptId: string;
  name: string;
  round?: number;
  members: PlanAccount[];
  dependencies: { order: number; name: string }[];
  dependencySkipped: boolean;
  reasonCode: string | null;
  state: PlanAccount['state'];
}

export function planGroups(preview: ExecutionPreview, bundle: AutomationBundle | undefined, targetKind: PlanTargetKind, targetId: string): PlanGroup[] {
  const queue = targetKind === 'queue' ? bundle?.queues.find(item => item.id === targetId) : undefined;
  const declared = targetKind === 'queue' ? preview.groups : preview.reasonCode === 'execution_script_missing'
    ? [] : [{ scriptId: targetId, reasonCode: preview.reasonCode }];
  const accounts = planAccounts(preview);
  const names = new Map(bundle?.scripts.map(item => [item.id, item.name]));
  const groups: PlanGroup[] = [];
  for (const descriptor of declared) {
    const id = descriptor.scriptId;
    const instances = declared.filter(item => item.scriptId === id).length;
    const prior = groups.filter(group => group.scriptId === id).length;
    const matching = accounts.filter(account => account.scriptId === id);
    const size = matching.length / instances;
    const members = matching.slice(prior * size, (prior + 1) * size);
    const required = queue?.successDependencies?.find(item => item.scriptId === id)?.requiredScriptIds;
    const dependencies = groups.filter(group => required?.includes(group.scriptId)).map(({ order, name }) => ({ order, name }));
    groups.push({ order: groups.length + 1, scriptId: id, name: names.get(id) ?? id,
      ...(instances > 1 ? { round: prior + 1 } : {}), members, dependencies, state: planState(members),
      dependencySkipped: members.length > 0 && members.every(item => item.reasonCode === 'execution_queue_dependency_not_successful'),
      reasonCode: descriptor.reasonCode });
  }
  return groups;
}

export function planState(accounts: readonly PlanAccount[]): PlanAccount['state'] {
  if (
    !accounts.length ||
    (accounts.every((item) => item.state !== 'ready') && accounts.some((item) => item.state === 'blocked'))
  )
    return 'blocked';
  return accounts.some((item) => item.state !== 'ready') ? 'attention' : 'ready';
}

export const planLabel = (state: PlanAccount['state']) =>
  t(state === 'ready' ? '可正常运行' : state === 'attention' ? '需要留意' : '无法运行');
export const planTone = (state: PlanAccount['state']) =>
  state === 'ready' ? 'success' : state === 'attention' ? 'warning' : 'danger';
export const planAccountLabel = (account: PlanAccount) => t(account.skipped ? '将跳过' : account.state === 'ready' ? '可运行' : '无法运行');
export const planNextStep = (code: string) => nextSteps[code] ? t(nextSteps[code]) : '';
export const planRound = (round: number) => t('第 {number} 轮').replace('{number}', String(round));
export const planDependency = (order: number, name: string) => t('第 {number} 组「{name}」').replace('{number}', String(order)).replace('{name}', name);

export const completionLabels: Record<ExecutionPreview['completionAction'], string> = {
  none: '', shutdown: '关机', hibernate: '休眠', restart: '重启', exit: '退出程序'
};

registerMessages({
  休眠: 'Hibernate',
  重启: 'Restart',
  退出程序: 'Exit application',
  运行完成操作: 'After completion',
  符合当前运行条件: 'Eligible under current conditions',
  '截图助手校验失败，请检查 Host 安装文件。': 'The capture helper failed verification. Check the Host installation files.',
  账号绑定未启用: 'The account binding is disabled',
  运行天数已用完: 'No run days remain',
  今日成功运行次数已达上限: 'The daily successful-run limit has been reached',
  专项插件未启用: 'The specialized plugin is disabled',
  '脚本实例配置丢失，请重新编辑或导入。': 'The script instance lost its configuration. Edit or import it again.',
  '此脚本实例的配置还在恢复。': 'This script instance still has configuration recovery pending.',
  前置任务未满足成功条件: 'A prerequisite has not succeeded',
  '没有可用的账号绑定，请启用账号及其脚本实例。':
    'No eligible account bindings. Enable an account and its script.',
  脚本实例不存在: 'The script instance is unavailable',
  调度队列不存在: 'The dispatch queue is unavailable',
  '此脚本实例尚未保存配置。': 'This script instance has no saved configuration.',
  '此计划没有可执行的任务。': 'This plan has no executable tasks.',
  '在计划与队列中加入脚本实例，再重新检查。': 'Add a script instance in Plans and Queues, then check again.',
  可正常运行: 'Ready',
  可运行: 'Runnable',
  需要留意: 'Needs attention',
  无法运行: 'Blocked',
  将跳过: 'Will skip',
  本次全部跳过: 'All entries will skip',
  '处理阻断项后重新检查计划。': 'Resolve the blocked entries and check the plan again.',
  无可用绑定: 'No eligible bindings',
  账号运行顺序: 'Account execution order',
  脚本组顺序: 'Script group order',
  计划状态汇总: 'Plan status summary',
  需以下脚本组全部成功: 'Requires these script groups to succeed',
  未满足: 'Not met',
  '前置成功条件未满足，本组将跳过。': 'The prerequisite is not met. This group will be skipped.',
  '第 {number} 轮': 'Round {number}',
  '第 {number} 组「{name}」': 'Group {number} · {name}',
  '今天无需重复运行，可等到下次计划。': 'No repeat run is needed today. Wait for the next scheduled run.',
  '在插件中心启用对应的专项插件后重新检查。': 'Enable the specialized extension in Extensions, then check again.',
  '在账号与配置中核对该脚本实例的绑定状态。': 'Check the script’s binding in Accounts and Configuration.',
  '在账号与配置中重新编辑或导入该绑定的配置。': 'Edit or import the binding’s configuration in Accounts and Configuration.',
  '在账号与配置中完成该绑定的配置。': 'Finish the binding’s configuration in Accounts and Configuration.',
  '在系统诊断中查看配置恢复状态，恢复后重新检查。': 'Check recovery in System Diagnostics, then check the plan again.',
  原因: 'Reason',
  运行计划检查: 'Run plan check',
  '当前检查通过，各账号符合运行条件。': 'The check passed. All accounts are eligible.',
  '部分任务将跳过或存在需要留意的事项，请查看下方原因。':
    'Some tasks will be skipped or need attention. Review the reasons below.',
  '当前没有可正常执行的计划，请先处理下方问题。': 'No runnable plan is available. Resolve the issues below.',
  '当前任务将被终止；如果这是调度队列，后续任务也不会继续执行。确定取消吗？':
    'The current task will be terminated. If this is a queue, subsequent tasks will not run. Cancel it?'
});
