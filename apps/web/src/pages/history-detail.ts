import { locale, registerMessages, t } from '../app/locale';
import { historyElapsed } from './history-row';
import type { ActiveStep } from '../control/execution';

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
  点击展开: 'Click to expand',
  行脚本日志: 'log lines',
  查看完整日志: 'View full log',
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
