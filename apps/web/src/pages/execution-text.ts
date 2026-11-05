import { locale, t } from '../app/locale';
import type { ActiveRun } from '../control/execution';

const phases: Record<string, string> = {
  preparing: '准备中',
  running: '运行中',
  pending: '等待中',
  succeeded: '成功',
  failed: '失败',
  cancelled: '已取消',
  completed: '已完成',
  skipped: '已跳过',
  restoring: '恢复配置中',
  collecting: '收集中',
  collected: '已收集',
  restored: '配置已恢复',
  stopping: '停止中',
  reconciliationRequired: '需要核对'
};

export function runPhase(value: string): string {
  return t(phases[value] || value);
}

export function runAttempt(run: ActiveRun): string {
  const value = run.ordinal + '/' + (run.display?.maximumAttempts || '—');
  return locale.value === 'en-US' ? 'Attempt ' + value : '第' + value + '次';
}
