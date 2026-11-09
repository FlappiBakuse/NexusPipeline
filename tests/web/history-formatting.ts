import assert from 'node:assert/strict';
import { locale, t } from '../../apps/web/src/app/locale';
import { historyRow, historyDate, outcomeName } from '../../apps/web/src/pages/history-row';
import '../../apps/web/src/pages/history-messages';
import { historyDuration, historyTime, historyReason, startedInvocations } from '../../apps/web/src/pages/history-detail';

const record = {
  finalAttempt: { runId: '', attemptId: '', ordinal: 1, engine: 'succeeded', business: 'succeeded', lifecycle: 'completed', recovery: 'restored', reasonCode: 'execution_completed' },
  endedAt: '2026-11-02T00:00:00Z'
} satisfies Parameters<typeof historyRow>[0];

export function run(): string[] {
  const cases: string[] = [];
  for (const language of ['zh-CN', 'en-US'] as const) {
    locale.value = language;
    const samples = language === 'zh-CN' ?
      [[0, '0 毫秒'], [999, '999 毫秒'], [999.6, '1 秒'], [1500, '1.5 秒'], [34200, '34.2 秒'], [60000, '1 分钟'], [90000, '1.5 分钟'], [3600000, '1 小时']] as const :
      [[0, '0 ms'], [999, '999 ms'], [999.6, '1 s'], [1500, '1.5 s'], [34200, '34.2 s'], [60000, '1 min'], [90000, '1.5 min'], [3600000, '1 h']] as const;
    for (const [value, duration] of samples) assert.equal(historyDuration(value), duration);
    const [label, exact, lower] = language === 'zh-CN' ? ['耗时', '1 分钟', '至少 1 分钟'] : ['Duration', '1 min', 'At least 1 min'];
    const row = { ...record, durationMilliseconds: 60000 };
    assert.equal(historyDuration(row.durationMilliseconds, 'checkpointLowerBound'), lower);
    assert.ok(historyRow({ ...row, durationKind: 'measured' }).description.endsWith(` · ${label} ${exact}`));
    assert.ok(historyRow({ ...row, durationKind: 'checkpointLowerBound' }).description.endsWith(` · ${label} ${lower}`));
    assert.equal(historyTime(null), t('未知'));
    assert.equal(historyDuration(-1), t('未知'));
    assert.equal(outcomeName('unknown'), language === 'zh-CN' ? '待核实' : 'Unverified');
    assert.equal(historyReason('execution_total_budget_exhausted'), language === 'zh-CN' ? '总运行时限已耗尽' : 'The total run time limit was reached');
    assert.equal(historyReason('execution_engine_succeeded'), language === 'zh-CN' ? '脚本进程已正常退出' : 'The script process exited normally');
    assert.equal(historyReason('execution_engine_failed'), language === 'zh-CN' ? '脚本进程异常退出' : 'The script process exited with an error');
    for (const [code, chinese, english] of [
      ['adapter_business_scope_empty', '本次运行没有选中的专项业务任务', 'No specialized business tasks were selected for this run'],
      ['adapter_business_task_failed', '专项任务失败', 'Specialized task failed'],
      ['adapter_business_task_partial', '专项任务部分完成', 'Specialized tasks partially completed'],
      ['adapter_business_tasks_succeeded', '专项任务已完成', 'Specialized tasks completed'],
      ['adapter_business_tasks_unverified', '专项任务结果待核实', 'Specialized task results need verification'],
      ['execution_step_stall_timeout', '脚本输出停滞超时', 'Script output inactivity timed out'],
      ['execution_step_total_timeout', '脚本运行时限已耗尽', 'The script run time limit was reached'],
      ['execution_binding_total_timeout', '实例总运行时限已耗尽', 'The instance run time limit was reached'],
      ['execution_success_grace_expired', '业务成功后脚本未及时退出，已停止进程', 'The script did not exit after business success and was stopped'],
      ['execution_business_failed', '业务判定失败', 'Business judgement failed'],
      ['execution_business_succeeded', '业务判定成功', 'Business judgement succeeded']
    ]) assert.equal(historyReason(code), language === 'zh-CN' ? chinese : english);
    assert.equal(historyDate('2024-02-29'), language === 'zh-CN' ? '2024年2月29日' : 'Feb 29, 2024');
    assert.equal(historyDate('2026-11-02'), language === 'zh-CN' ? '2026年11月2日' : 'Nov 2, 2026');
    cases.push('History locale and duration precision remain exact in ' + language);
  }
  locale.value = 'zh-CN';
  assert.equal(historyDuration(60000), '1 分钟');
  cases.push('Changing locale back refreshes existing history formatting');
  const timing = { startedAt: null, endedAt: null, durationMilliseconds: null };
  const called = { ordinal: 1, engine: 'Succeeded', business: 'Succeeded', reasonCode: 'execution_completed', ...timing };
  const unstarted = { ordinal: 2, engine: 'NotStarted', business: 'Unverified', reasonCode: 'execution_not_started', ...timing };
  assert.deepEqual(startedInvocations({ invocations: [called, unstarted] }), [called]);
  assert.deepEqual(startedInvocations({ invocations: [unstarted] }), []);
  cases.push('Not-started receipts stay outside actual invocation cards and counts');
  return cases;
}

export function benchmark(iterations: number): number {
  locale.value = 'zh-CN';
  let characters = 0;
  for (let index = 0; index < iterations; index++) {
    characters += historyDuration(index * 101).length;
    characters += historyRow({ ...record, durationMilliseconds: index * 101 }).description.length;
  }
  return characters;
}
