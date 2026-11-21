import type { RecordRow } from '../control/models';
import type { ActiveStep, RunTask } from '../control/execution';
import { locale, registerMessages, t } from '../app/locale';
import { computed } from 'vue';

// ICU formatters own native data; keep one set for the active UI locale across history views.
const durationFormats = computed(() => [
  new Intl.NumberFormat(locale.value, { maximumFractionDigits: 1 }),
  new Intl.NumberFormat(locale.value, { minimumFractionDigits: 1, maximumFractionDigits: 1 })
]);
const rangeFormat = computed(() => new Intl.DateTimeFormat(locale.value, { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' }));

export function historyElapsed(value: unknown, kind?: unknown): string {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) return t('未知');
  const milliseconds = Math.round(value);
  const [amount, unit, englishUnit] = milliseconds < 1000 ? [milliseconds, '毫秒', 'ms'] :
    milliseconds < 60000 ? [milliseconds / 1000, '秒', 's'] :
    milliseconds < 3600000 ? [milliseconds / 60000, '分钟', 'min'] : [milliseconds / 3600000, '小时', 'h'];
  return (kind === 'checkpointLowerBound' ? t('至少') + ' ' : '') +
    durationFormats.value[0]!.format(Number(amount)) + ' ' + (locale.value === 'en-US' ? englishUnit : unit);
}

// Range endpoints are calendar dates; formatting must preserve their components across time zones.
export const historyDate = (value: string): string => rangeFormat.value.format(new Date(value + 'T00:00:00Z'));

export function historyRow(row: Pick<RecordRow, 'finalAttempt'> & Partial<RecordRow>): { label: string; tone: string; description: string } {
  const { business, lifecycle } = row.finalAttempt;
  let label = '待核实',
    tone = 'neutral';
  if (lifecycle === 'cancelled') label = '已取消';
  else if (lifecycle === 'interrupted') {
    label = '已中断';
    tone = 'warning';
  } else if (lifecycle === 'blocked') {
    label = '已阻断';
    tone = 'danger';
  } else if (lifecycle === 'skipped') label = '已跳过';
  else if (business === 'succeeded') {
    label = '✓ 完成';
    tone = 'success';
  } else if (business === 'failed') {
    label = '✕ 失败';
    tone = 'danger';
  } else if (business === 'partial') {
    label = '部分完成';
    tone = 'warning';
  } else if (row.finalAttempt.engine === 'failed') {
    label = '✕ 失败';
    tone = 'danger';
  }
  const date = new Date(String(row.endedAt));
  const two = (value: number) => String(value).padStart(2, '0');
  const time =
    locale.value === 'zh-CN'
      ? `${date.getFullYear()}年${date.getMonth() + 1}月${date.getDate()}日 ${two(date.getHours())}:${two(date.getMinutes())}:${two(date.getSeconds())}`
      : date.toLocaleString(locale.value);
  return { label: t(label), tone, description: time + ' · ' + t('耗时') + ' ' +
    historyElapsed(row.durationMilliseconds, row.durationKind) };
}

const outcomeNames: Record<string, string> = {
  unknown: '待核实',
  unverified: '待核实',
  succeeded: '成功',
  partial: '部分成功',
  failed: '失败',
  notApplicable: '不适用',
  cancelled: '已取消'
};
export const outcomeName = (value: string): string => t(outcomeNames[value] || value);
registerMessages({ 已停用: 'Disabled', 未安排核验: 'Not selected for verification', 未执行: 'Not executed',
  结果未保存: 'Result not saved', 已满足: 'Already satisfied',
  配置中已停用此任务: 'This task is disabled in the configuration',
  本次运行未选中此任务: 'This task was not selected for this run',
  所在步骤已跳过: 'The containing step was skipped',
  运行取消前未取得任务结果: 'The run was cancelled before a task result was recorded',
  运行结束前未开始此任务: 'This task did not start before the run ended',
  本次运行条件不适用: 'The task conditions did not apply to this run',
  '条件已满足，无需执行': 'The conditions were already satisfied',
  未记录可核验的任务结果: 'No verifiable task result was recorded',
  已记录成功结果: 'A successful result was recorded',
  已记录失败结果: 'A failed result was recorded',
  仅部分操作成功: 'Only some operations succeeded' });

const taskResultReasons: Record<RunTask['state'], string> = {
  succeeded: '已记录成功结果',
  failed: '已记录失败结果',
  partial: '仅部分操作成功',
  unknown: '未记录可核验的任务结果'
};

export function historyTaskStatus(task: RunTask, step: Pick<ActiveStep, 'phase' | 'reasonCode'>) {
  const neutral = (label: string, reason: string) => ({ label: t(label), tone: 'neutral', reason: t(reason) });
  if (!task.isEnabled) return neutral('已停用', '配置中已停用此任务');
  if (!task.isSelected) return neutral('未安排核验', '本次运行未选中此任务');
  if (step.phase === 'skipped') return neutral('已跳过', '所在步骤已跳过');
  if (task.state === 'unknown' && step.reasonCode === 'execution_cancel_requested' && step.phase !== 'notExecuted')
    return neutral('已取消', '运行取消前未取得任务结果');
  if (step.phase === 'notExecuted' || task.completionKind === 'notExecuted')
    return neutral('未执行', '运行结束前未开始此任务');
  if (task.completionKind === 'inapplicable') return neutral('不适用', '本次运行条件不适用');
  if (task.completionKind === 'satisfied') return neutral('已满足', '条件已满足，无需执行');
  if (task.state === 'unknown' && !task.reasonCode) return neutral('结果未保存', taskResultReasons.unknown);
  const tone = task.state === 'succeeded' ? 'success' : task.state === 'failed' ? 'danger' : task.state === 'partial' ? 'warning' : 'neutral';
  return { label: outcomeName(task.state), tone, reason: t(taskResultReasons[task.state]) };
}
export const recordBusiness = (row: RecordRow): string =>
  outcomeName(row.finalAttempt.business);
const lifecycleNames: Record<string, string> = {
  completed: '已完成',
  cancelled: '已取消',
  interrupted: '已中断',
  blocked: '已阻断',
  skipped: '已跳过'
};
export const recordLifecycle = (row: RecordRow): string =>
  t(lifecycleNames[row.finalAttempt.lifecycle] || '待核实');
