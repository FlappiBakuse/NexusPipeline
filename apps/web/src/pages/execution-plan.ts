import type { ExecutionPreview } from '../control/execution';
import { locale, registerMessages, t } from '../app/locale';

export interface PlanAccount {
  scriptId: string;
  userId: string;
  state: 'ready' | 'attention' | 'blocked';
  skipped: boolean;
  reason: string;
  successfulRunsToday?: number;
  maximumSuccessfulRunsPerDay?: number;
}

const reasons: Record<string, string> = {
  execution_inputs_verified: '符合当前运行条件',
  execution_binding_disabled: '账号绑定未启用',
  execution_run_days_exhausted: '运行天数已用完',
  execution_daily_success_limit: '今日成功运行次数已达上限',
  execution_extension_disabled: '专项插件未启用',
  execution_queue_dependency_not_successful: '前置任务未满足成功条件',
  execution_no_enabled_bindings: '没有可用的账号绑定，请启用账号及其脚本实例。',
  execution_script_missing: '脚本实例不存在',
  execution_queue_empty: '此计划没有可执行的任务。'
};

export function planReason(code: string): string {
  return t(reasons[code] || code);
}

export function planAccounts(preview: ExecutionPreview): PlanAccount[] {
  return preview.items.map((item) => {
    const { disposition, successfulRunsToday: successes, maximumSuccessfulRunsPerDay: maximum } = item;
    const reason = planReason(item.reasonCode);
    const count =
      item.reasonCode === 'execution_daily_success_limit' && successes != null && maximum != null
        ? locale.value === 'zh-CN'
          ? `（${successes}/${maximum}）`
          : ` (${successes}/${maximum})`
        : '';
    return {
      scriptId: item.scriptId,
      userId: item.userId,
      state: disposition === 'runnable' ? 'ready' : disposition === 'skipped' ? 'attention' : 'blocked',
      skipped: disposition === 'skipped',
      reason: reason + count,
      ...(successes == null ? {} : { successfulRunsToday: successes }),
      ...(maximum == null ? {} : { maximumSuccessfulRunsPerDay: maximum })
    };
  });
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

export const completionLabels: Record<ExecutionPreview['completionAction'], string> = {
  none: '', shutdown: '关机', hibernate: '休眠', restart: '重启', exit: '退出程序'
};

registerMessages({
  休眠: 'Hibernate',
  重启: 'Restart',
  退出程序: 'Exit application',
  运行完成操作: 'After completion',
  符合当前运行条件: 'Eligible under current conditions',
  账号绑定未启用: 'The account binding is disabled',
  运行天数已用完: 'No run days remain',
  今日成功运行次数已达上限: 'The daily successful-run limit has been reached',
  专项插件未启用: 'The specialized plugin is disabled',
  前置任务未满足成功条件: 'A prerequisite has not succeeded',
  '没有可用的账号绑定，请启用账号及其脚本实例。':
    'No eligible account bindings. Enable an account and its script.',
  脚本实例不存在: 'The script instance is unavailable',
  '此计划没有可执行的任务。': 'This plan has no executable tasks.',
  可正常运行: 'Ready',
  需要留意: 'Needs attention',
  无法运行: 'Blocked',
  将跳过: 'Will skip',
  原因: 'Reason',
  运行计划检查: 'Run plan check',
  '当前检查通过，各账号符合运行条件。': 'The check passed. All accounts are eligible.',
  '部分任务将跳过或存在需要留意的事项，请查看下方原因。':
    'Some tasks will be skipped or need attention. Review the reasons below.',
  '当前没有可正常执行的计划，请先处理下方问题。': 'No runnable plan is available. Resolve the issues below.',
  '当前任务将被终止；如果这是调度队列，后续任务也不会继续执行。确定取消吗？':
    'The current task will be terminated. If this is a queue, subsequent tasks will not run. Cancel it?'
});
