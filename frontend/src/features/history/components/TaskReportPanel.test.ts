import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import TaskReportPanel from "./TaskReportPanel.vue";
import type { TaskPlan, TaskReport } from "../utils/taskTypes";
vi.mock("../../../platform/i18n", () => ({ t: (key: string) => key }));
const report = (): TaskReport => ({
  schemaVersion: 1, runId: "record", revision: 1, userId: "user", scriptInstanceId: "script", lifecycleOutcome: "completed",
  originalPlan: { coverage: "partial", pluginVersion: "0.3.0", generatedAt: "2026-09-21", diagnostics: [],
    tasks: [{ id: "mail", name: "Mail", parentId: null, role: "business", enabled: true, detection: "supported", retryRisk: "safe", order: 0 }] },
  finalTaskResults: [{ taskId: "mail", status: "succeeded", reasonCode: "claimed", lastAttemptId: "second", evidence: [] }],
  summary: { tone: "ok", outcome: "completed", counts: { succeeded: 1 }, recovered: true },
  attemptReports: [{ attemptId: "first", number: 1, selectedTaskIds: ["mail"], taskResults: [{ taskId: "mail", status: "failed", reasonCode: "failure", lastAttemptId: "first",
    evidence: [{ sourceId: "stdout", epoch: 2, sequence: 4, ruleId: "failure" }] }] },
    { attemptId: "second", number: 2, selectedTaskIds: [], taskResults: [] }],
  evidenceLines: [{ attemptId: "first", sourceId: "stdout", epoch: 2, sequence: 4, text: "Exact evidence" }],
});
describe("task history", () => {
  it("only presents enabled tasks in plans and frozen reports", () => {
    const data = report();
    data.originalPlan.tasks.push({ ...data.originalPlan.tasks[0], id: "disabled", name: "Disabled task", enabled: false });
    const variants: Array<{ plan?: TaskPlan; report?: TaskReport; layout?: "cards" | "steps" }> = [{ plan: data.originalPlan }, { report: data }, { report: data, layout: "steps" }];
    for (const props of variants) {
      const wrapper = mount(TaskReportPanel, { props });
      expect(wrapper.text()).toContain("Mail");
      expect(wrapper.text()).not.toContain("Disabled task");
      expect(wrapper.text()).not.toContain("supported ·");
      wrapper.unmount();
    }
  });

  it("keeps legacy records unknown and displays frozen attempts with their exact evidence", async () => {
    const legacy = mount(TaskReportPanel);
    expect(legacy.text()).toContain("tasks.legacy"); legacy.unmount();
    const wrapper = mount(TaskReportPanel, { props: { report: report() } });
    expect(wrapper.text()).toContain("tasks.recovered");
    expect(wrapper.text()).toContain("tasks.not_retried");
    await wrapper.get('button[aria-expanded]').trigger("click");
    expect(wrapper.get('pre').isVisible()).toBe(true);
    expect(wrapper.get('pre').text()).toContain("Exact evidence");
    expect(wrapper.findAll('button').some(button => button.text().includes('tasks.evidence'))).toBe(false);
    const missing = report(); missing.evidenceLines = [];
    await wrapper.setProps({ report: missing });
    expect(wrapper.get('pre').text()).toContain("tasks.evidence_unavailable");
    wrapper.unmount();
  });
  it("opens the linked task and focuses its attempt without requiring a date filter", async () => {
    window.location.hash = "/history?recordId=record&taskId=mail&attemptId=first";
    const wrapper = mount(TaskReportPanel, { props: { report: report() }, attachTo: document.body });
    await flushPromises();
    expect(wrapper.get('button[aria-expanded]').attributes('aria-expanded')).toBe('true');
    expect(document.activeElement?.textContent).toContain("tasks.status.failed");
    wrapper.unmount(); window.location.hash = "";
  });
  it("opens ancestors of a linked child and keeps parent and child attempt evidence separate", async () => {
    const data = report();
    data.originalPlan.tasks.unshift({ ...data.originalPlan.tasks[0], id: 'rewards', name: 'Rewards', order: -1 });
    data.originalPlan.tasks[1].parentId = 'rewards';
    data.attemptReports[0].taskResults.unshift({ taskId: 'rewards', status: 'partial', reasonCode: 'partial', lastAttemptId: 'first', evidence: [] });
    window.location.hash = '/history?recordId=record&taskId=mail&attemptId=first';
    const wrapper = mount(TaskReportPanel, { props: { report: data }, attachTo: document.body });
    await flushPromises();
    expect(wrapper.findAll('button[aria-expanded]').map(button => button.attributes('aria-expanded'))).toEqual(['true', 'true']);
    expect(document.activeElement?.textContent).toContain('Exact evidence');
    expect(document.activeElement?.textContent).not.toContain('tasks.status.partial');
    wrapper.unmount(); window.location.hash = '';
  });
  it("renders steps without disclosure controls, parent subtitles or evidence details", () => {
    const data = report();
    data.originalPlan.tasks[0].parentId = 'disabled-parent';
    data.originalPlan.tasks.push({ ...data.originalPlan.tasks[0], id: 'disabled-parent', name: 'Parent subtitle', enabled: false });
    const wrapper = mount(TaskReportPanel, { props: { report: data, layout: 'steps' } });
    expect(wrapper.get('li').text()).toContain('1');
    expect(wrapper.get('li').text()).toContain('Mail');
    expect(wrapper.get('li').text()).toContain('tasks.status.succeeded');
    expect(wrapper.find('button[aria-expanded]').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('Parent subtitle');
    expect(wrapper.text()).not.toContain('Exact evidence');
    wrapper.unmount();
  });
  it("lets users dismiss plan warnings and restores them after a new plan is read", async () => {
    const wrapper = mount(TaskReportPanel, { props: { plan: report().originalPlan, stale: true } });
    expect(wrapper.get('[role="status"]').text()).toContain('tasks.stale');
    expect(wrapper.get('[role="status"]').text()).toContain('tasks.coverage_help');
    await wrapper.get('button[aria-label="common.close"]').trigger('click');
    expect(wrapper.find('[role="status"]').exists()).toBe(false);
    await wrapper.setProps({ plan: report().originalPlan });
    expect(wrapper.find('[role="status"]').exists()).toBe(true);
    wrapper.unmount();
  });
});
