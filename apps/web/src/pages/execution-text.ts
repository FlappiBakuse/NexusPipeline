import { locale, t } from '../app/locale';
import { currentStep, finishedSteps, type ActiveRun } from '../control/execution';
import type { AutomationBundle } from '../control/wire.generated';

const phases: Record<string, string> = {
  preparing: '准备中',
  running: '运行中',
  pending: '等待中',
  succeeded: '成功',
  failed: '失败',
  cancelled: '已取消',
  completed: '已完成',
  skipped: '已跳过',
  notExecuted: '未执行',
  committed: '已完成',
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

export function runTitle(run: ActiveRun): string {
  return run.display?.targetName || run.runId;
}

export function runScriptName(run: ActiveRun, scripts?: AutomationBundle['scripts']): string {
  const scriptId = currentStep(run)?.scriptId;
  return scripts?.find(item => item.id === scriptId)?.name || scriptId || '—';
}

export function runPhaseTone(phase: string): 'danger' | 'warning' | 'info' {
  if (phase === 'reconciliationRequired') return 'danger';
  return phase === 'restoring' || phase === 'stopping' ? 'warning' : 'info';
}

export function runProgress(run: ActiveRun): number {
  if (run.display?.targetKind === 'queue')
    return Math.round(100 * finishedSteps(run) / Math.max(1, run.steps.length));
  const { ordinal, maximum } = invocationAttempt(run);
  return Math.min(100, Math.round(100 * ordinal / maximum));
}

export function invocationAttempt(run: ActiveRun): { ordinal: number; maximum: number } {
  const step = currentStep(run);
  return { ordinal: Math.max(1, step?.invocationOrdinal ?? run.ordinal),
    maximum: step?.maximumInvocations ?? run.display?.maximumAttempts ?? Math.max(1, run.ordinal) };
}

export function runAttempt(run: ActiveRun): string {
  const attempt = invocationAttempt(run);
  const value = attempt.ordinal + '/' + attempt.maximum;
  return locale.value === 'en-US' ? 'Attempt ' + value : '第' + value + '次';
}
