import { mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import TaskReportPanel from './TaskReportPanel.vue';
import type { TaskReport } from '../utils/taskTypes';
import { taskOutcomeLabel } from '../utils/taskLabels';
import zh from '../../../../public/i18n/zh-CN.json';

vi.mock('../../../platform/i18n', () => ({
  getLocale: () => 'zh-CN',
  t: (key: string, args: Record<string, unknown> = {}, fallback?: string) => {
    const messages = zh as Record<string, string>;
    return (messages[key] || fallback || key).replace(/\{(\w+)\}/g, (_, name: string) => String(args[name] ?? ''));
  },
}));

function report(status: string, schemaVersion = 2): TaskReport {
  return {
    schemaVersion, semanticsVersion: schemaVersion === 2 ? 'daily-flow-v1' : 'provider-execution-v1',
    runId: 'run', revision: 1, userId: 'a', scriptInstanceId: 'script', lifecycleOutcome: 'completed',
    originalPlan: { tasks: [{ id: 'daily', name: '日常', parentId: null, role: 'business', enabled: true,
      detection: 'supported', retryRisk: 'safe', order: 0 }], coverage: 'complete', pluginVersion: '1', generatedAt: '', diagnostics: [] },
    finalTaskResults: [{ taskId: 'daily', status, reasonCode: '', lastAttemptId: 'attempt', evidence: [] }],
    attemptReports: [], summary: { tone: 'success', outcome: 'all_satisfied', counts: { total: 1, succeeded: status === 'succeeded' ? 1 : 0 }, recovered: true },
    incidents: [{ attemptId: 'attempt', incident: { id: 'incident', taskId: null, scopeId: 'scope', executionOrdinal: 1,
      kind: 'business_failed', resolution: 'recovered', reasonCode: '内部恢复记录', evidence: [] } }],
  };
}

describe('daily report projection', () => {
  it('explains host recognition outcomes without claiming unknown tasks succeeded', () => {
    const recognition = { ...report('succeeded'), schemaVersion: 3, semanticsVersion: 'daily-flow-v2' };
    expect(taskOutcomeLabel('execution.success_with_partial_verification', 0, recognition,
      'execution.success_with_partial_verification')).toBe('运行成功：已知任务完成，其他任务未逐项核验。');
    expect(taskOutcomeLabel('', 0, recognition, 'execution.success_process_exit_only'))
      .toBe('运行成功：自动化进程正常结束，任务未逐项核验。');
    expect(taskOutcomeLabel('execution.unconfirmed_worker', 0, recognition, 'execution.unconfirmed_worker'))
      .toBe('尚未确认自动化进程正常结束。');
    expect(taskOutcomeLabel('third-party literal', 0, recognition, 'external.reason')).toBe('third-party literal');
  });
  it('rejects missing or mismatched semantics without exposing old task facts', () => {
    for (const invalid of [undefined, 'unknown', 'provider-execution-v1']) {
      const old = report('succeeded');
      old.semanticsVersion = invalid;
      old.originalPlan.tasks[0]!.name = 'unsupported-private-task';
      const wrapper = mount(TaskReportPanel, { props: { report: old } });
      expect(wrapper.text()).not.toContain('unsupported-private-task');
      expect(wrapper.text()).toContain('不支持此历史格式');
      wrapper.unmount();
    }
  });
  it('shows recovered daily work as completed without resurfacing the recovered incident', () => {
    const wrapper = mount(TaskReportPanel, { props: { report: report('succeeded') } });
    expect(wrapper.text()).toContain('完成');
    expect(wrapper.text()).not.toContain('内部恢复记录');
    expect(wrapper.text()).not.toContain('未核验');
    wrapper.unmount();
  });
  it('keeps partial completion distinct from failure and preserves provider incident evidence', async () => {
    const wrapper = mount(TaskReportPanel, { props: { report: report('partial') } });
    expect(wrapper.text()).toContain('部分完成');
    expect(wrapper.text()).not.toContain('部分失败');
    await wrapper.setProps({ report: report('partial', 1) });
    expect(wrapper.text()).toContain('部分失败');
    expect(wrapper.text()).toContain('内部恢复记录');
    wrapper.unmount();
  });
});
