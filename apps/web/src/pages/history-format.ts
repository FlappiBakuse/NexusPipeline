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

export function startedInvocations(step: Pick<ActiveStep, 'invocations'>) {
  return (step.invocations || []).filter(invocation => invocation.engine !== 'NotStarted');
}

registerMessages({
  结果: 'Result',
  运行模式: 'Run mode',
  运行用户: 'Account',
  尝试次数: 'Attempts',
  运行调用次数: 'Process invocations',
  运行总耗时: 'Total run duration',
  开始时间: 'Started',
  结束时间: 'Ended',
  结果说明: 'Result details',
  专项任务: 'Specialized tasks',
  本次运行没有可用的任务明细: 'No task details are available for this run',
  手动运行: 'Manual',
  自动运行: 'Automatic',
  时间: 'Time',
  原因: 'Reason',
  脚本日志: 'Script log',
  '正在读取脚本日志…': 'Reading the script log…',
  没有可用的日志: 'No log is available',
  运行已完成: 'Run completed',
  运行已取消: 'Run cancelled',
  运行已中断: 'Run interrupted',
  尚未启动脚本: 'The script was not started',
  日志已匹配成功关键字: 'The log matched a success keyword',
  日志已匹配失败关键字: 'The log matched a failure keyword',
  已采集运行截图: 'Run screenshot captured',
  运行输出尚未核验: 'Run output has not been verified',
  脚本进程已正常退出: 'The script process exited normally',
  脚本进程异常退出: 'The script process exited with an error',
  总运行时限已耗尽: 'The total run time limit was reached',
  脚本输出停滞超时: 'Script output inactivity timed out',
  脚本运行时限已耗尽: 'The script run time limit was reached',
  实例总运行时限已耗尽: 'The instance run time limit was reached',
  '业务成功后脚本未及时退出，已停止进程': 'The script did not exit after business success and was stopped',
  业务判定失败: 'Business judgement failed',
  业务判定成功: 'Business judgement succeeded',
  宿主中断了运行: 'The host interrupted the run',
  截图采集已超时: 'Screenshot capture timed out',
  截图画面未通过核验: 'The screenshot frame could not be verified',
  本次运行没有选中的专项业务任务: 'No specialized business tasks were selected for this run',
  专项任务失败: 'Specialized task failed',
  专项任务部分完成: 'Specialized tasks partially completed',
  专项任务已完成: 'Specialized tasks completed',
  可核验范围已完成: 'Verifiable scope completed',
  '启动器已退出，自动化结果待核实': 'The launcher exited; the automation result needs verification',
  未检测到任务日志: 'No task logs were detected',
  未检测到任务终态: 'No terminal task result was detected',
  任务日志不连续: 'Task logs are incomplete',
  专项任务观察失败: 'Specialized task observation failed',
  缺少自动化进程身份: 'The automation process identity is unavailable',
  依赖步骤未成功: 'A prerequisite did not complete successfully',
  今日成功运行次数已达上限: 'The daily successful-run limit has been reached',
  上游结束前未执行此任务: 'The upstream program ended before this task ran',
  专项任务结果待核实: 'Specialized task results need verification'
});
export function historyTime(value: unknown): string {
  if (typeof value !== 'string' || !Number.isFinite(Date.parse(value))) return t('未知');
  const date = new Date(value);
  const two = (number: number): string => String(number).padStart(2, '0');
  return locale.value === 'zh-CN'
    ? `${date.getFullYear()}年${date.getMonth() + 1}月${date.getDate()}日 ${two(date.getHours())}:${two(date.getMinutes())}:${two(date.getSeconds())}`
    : date.toLocaleString(locale.value);
}
export function historyDuration(value: unknown, kind?: unknown): string {
  return historyElapsed(value, kind);
}
export function attemptTitle(ordinal: number): string {
  return locale.value === 'zh-CN' ? `第 ${ordinal} 次尝试` : `Attempt ${ordinal}`;
}
export function historyReason(value: unknown): string {
  const code = String(value || '未知');
  const labels: Record<string, string> = {
    execution_not_started: '尚未启动脚本',
    execution_completion_missing: '脚本已退出，未检测到约定的完成标记',
    configuration_sync_capture_rejected: '配置同步采集失败，已保留旧快照',
    adapter_retry_unavailable: '专项重试建议读取失败，已停止重试',
    judgement_keyword_succeeded: '日志已匹配成功关键字',
    judgement_keyword_failed: '日志已匹配失败关键字',
    execution_capture_frame_verified: '已采集运行截图',
    execution_capture_deadline: '截图采集已超时',
    execution_capture_frame_rejected: '截图画面未通过核验',
    execution_output_unverified: '运行输出尚未核验',
    adapter_business_scope_empty: '本次运行没有选中的专项业务任务',
    adapter_business_task_failed: '专项任务失败',
    adapter_business_task_partial: '专项任务部分完成',
    adapter_business_tasks_succeeded: '专项任务已完成',
    adapter_business_verified_scope_succeeded: '可核验范围已完成',
    adapter_business_launcher_unverified: '启动器已退出，自动化结果待核实',
    adapter_task_missing_logs: '未检测到任务日志',
    adapter_task_missing_terminal: '未检测到任务终态',
    baah_terminal_missing: '未检测到任务终态',
    adapter_log_gap: '任务日志不连续',
    adapter_observation_unavailable: '专项任务观察失败',
    bettergi_upstream_process_identity_required: '缺少自动化进程身份',
    execution_queue_dependency_not_successful: '依赖步骤未成功',
    execution_daily_success_limit: '今日成功运行次数已达上限',
    adapter_task_not_executed: '上游结束前未执行此任务',
    adapter_business_tasks_unverified: '专项任务结果待核实',
    execution_engine_succeeded: '脚本进程已正常退出',
    execution_engine_failed: '脚本进程异常退出',
    execution_total_budget_exhausted: '总运行时限已耗尽',
    execution_step_stall_timeout: '脚本输出停滞超时',
    execution_step_total_timeout: '脚本运行时限已耗尽',
    execution_binding_total_timeout: '实例总运行时限已耗尽',
    execution_success_grace_expired: '业务成功后脚本未及时退出，已停止进程',
    execution_business_failed: '业务判定失败',
    execution_business_succeeded: '业务判定成功',
    host_interrupted: '宿主中断了运行',
    execution_completed: '运行已完成',
    execution_cancelled: '运行已取消',
    execution_cancel_requested: '运行已取消',
    execution_interrupted: '运行已中断'
  };
  return t(labels[code] || code);
}

export function localDate(date: Date): string {
  return [date.getFullYear(), String(date.getMonth() + 1).padStart(2, '0'), String(date.getDate()).padStart(2, '0')].join('-');
}
export function timeRange(start: string, end: string): { from: string; until: string } {
  const last = new Date(end + 'T00:00:00');
  last.setDate(last.getDate() + 1);
  return { from: new Date(start + 'T00:00:00').toISOString(), until: last.toISOString() };
}
