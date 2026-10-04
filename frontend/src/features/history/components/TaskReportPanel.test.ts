import { mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import TaskReportPanel from './TaskReportPanel.vue';
import type { TaskReport } from '../utils/taskTypes';
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
    schemaVersion, semanticsVersion: schemaVersion === 2 ? 'daily-flow-v1' : undefined,
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
  it('shows recovered daily work as completed without resurfacing the recovered incident', () => {
    const wrapper = mount(TaskReportPanel, { props: { report: report('succeeded') } });
    expect(wrapper.text()).toContain('完成');
    expect(wrapper.text()).not.toContain('内部恢复记录');
    expect(wrapper.text()).not.toContain('未核验');
    wrapper.unmount();
  });
  it('keeps partial completion distinct from failure and preserves legacy incident evidence', async () => {
    const wrapper = mount(TaskReportPanel, { props: { report: report('partial') } });
    expect(wrapper.text()).toContain('部分完成');
    expect(wrapper.text()).not.toContain('部分失败');
    await wrapper.setProps({ report: report('partial', 1) });
    expect(wrapper.text()).toContain('部分失败');
    expect(wrapper.text()).toContain('内部恢复记录');
    wrapper.unmount();
  });
});
