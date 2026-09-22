import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import TaskReportPanel from "./TaskReportPanel.vue";
import type { TaskPlan, TaskReport } from "../utils/taskTypes";
vi.mock("../../../platform/i18n", () => ({ getLocale: () => 'en-US', t: (key: string, args?: {count: number; done: number}) =>
  args && (key === 'tasks.progress' || key === 'tasks.enabled_count') ? `${key} ${args.done}/${args.count}` : key }));
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
  it('shows frozen plan notes in preview/history, deduplicates runtime notes and excludes disabled owners', () => {
    const data = report();
    data.originalPlan.tasks.push({ ...data.originalPlan.tasks[0], id: 'disabled', name: 'Disabled', enabled: false });
    data.originalPlan.diagnostics = [
      { code: 'plugin.option', taskId: 'mail', message: 'Fallback', reasonText: { kind: 'plugin', key: 'option.note', args: {}, fallback: 'Fallback' } },
      { code: 'legacy.limit', message: 'Legacy detection limit' },
      { code: 'disabled.note', taskId: 'disabled', message: 'Invisible disabled selection' }];
    data.originalPlan.displaySnapshot = { pluginId: 'arbitrary', pluginVersion: '1', defaultLocale: 'en-US', localizationHash: 'hash',
      messages: { 'en-US': { 'option.note': '<em>Frozen option</em>' } } };
    data.displaySnapshot = data.originalPlan.displaySnapshot;
    data.diagnostics = [data.originalPlan.diagnostics[0], { code: 'runtime.limit', message: 'Runtime detection limit' }];
    const variants: Array<{ plan?: TaskPlan; report?: TaskReport; layout?: 'cards' | 'steps' }> = [
      { plan: data.originalPlan }, { report: data }, { report: data, layout: 'steps' }];
    for (const props of variants) {
      const wrapper = mount(TaskReportPanel, { props });
      const notes = wrapper.get('[aria-label="tasks.plan_notes"]');
      expect(notes.text()).toContain('Mail ·');
      expect(notes.text().split('<em>Frozen option</em>')).toHaveLength(2);
      expect(notes.text()).toContain('Legacy detection limit');
      expect(notes.text()).not.toContain('Invisible disabled selection');
      expect(notes.find('em').exists()).toBe(false);
      if ('report' in props) expect(notes.text()).toContain('Runtime detection limit');
      wrapper.unmount();
    }
  });
  it('shows configuration assessment and keeps an admission block separate from task failures', () => {
    const data = report();
    data.originalPlan.configAssessment = { schemaVersion: '1', checks: [{
      ruleId: 'example.configuration', evaluation: 'violated', severity: 'error', executionEffect: 'block',
      scope: { kind: 'binding' }, locations: [], actions: [{ kind: 'refresh_plan' }],
      reasonText: { kind: 'literal', value: 'Configuration needs repair' },
    }] };
    data.originalPlan.currentReadiness = {
      state: 'blocked', stale: false, checkedAt: '2026-09-22T00:00:00Z', assessmentId: 'assessment',
      configRevision: 'revision', contextFingerprint: 'fingerprint',
    };
    data.admissionBlocked = { reasonCode: 'tasks.admission_blocked', message: 'blocked' };
    data.summary = undefined;
    data.finalTaskResults = [];
    data.attemptReports = [];
    data.incidents = [];
    const wrapper = mount(TaskReportPanel, { props: { report: data } });
    expect(wrapper.get('[role="alert"]').text()).toContain('tasks.admission_blocked');
    expect(wrapper.get('[aria-label="tasks.config.title"]').text()).toContain('Configuration needs repair');
    expect(wrapper.get('[aria-label="tasks.config.title"]').text()).toContain('tasks.readiness.blocked');
    expect(wrapper.text()).toContain('tasks.progress 0/1');
    wrapper.unmount();
  });
  it('retains unassigned incident evidence without attributing it to a task', () => {
    const data = report();
    data.incidents = [{ attemptId: 'first', incident: { id: 'unassigned', taskId: null, scopeId: 'unknown',
      executionOrdinal: 1, kind: 'unattributed_error', resolution: 'open', reasonCode: 'no-scope',
      reasonText: { kind: 'literal', value: 'Unassigned failure' },
      evidence: data.attemptReports[0].taskResults[0].evidence } }];
    const wrapper = mount(TaskReportPanel, { props: { report: data } });
    expect(wrapper.text()).toContain('tasks.incident.unattributed');
    expect(wrapper.text()).toContain('Unassigned failure');
    expect(wrapper.findAll('pre').some(element => element.isVisible() && element.text() === 'Exact evidence')).toBe(true);
    expect(wrapper.text()).toContain('tasks.progress 1/1');
    wrapper.unmount();
  });
  it('shows recovered incidents with frozen reasons without changing task counts', async () => {
    const data = report();
    const incident = { id: 'one', taskId: 'mail', scopeId: 'scope', executionOrdinal: 1,
      kind: 'transient_error', resolution: 'open' as const, reasonCode: 'plugin.code',
      reasonText: { kind: 'plugin' as const, key: 'custom.reason', args: {}, fallback: 'Fallback' },
      evidence: data.attemptReports[0].taskResults[0].evidence };
    data.incidents = [{ attemptId: 'first', incident },
      { attemptId: 'first', incident: { ...incident, resolution: 'recovered' } }];
    data.displaySnapshot = { pluginId: 'arbitrary', pluginVersion: '1', defaultLocale: 'en-US', localizationHash: 'hash',
      messages: { 'en-US': { 'custom.reason': '<b>Frozen reason</b>' } } };
    const wrapper = mount(TaskReportPanel, { props: { report: data } });
    await wrapper.get('button[aria-expanded]').trigger('click');
    expect(wrapper.text()).toContain('tasks.incident.recovered');
    expect(wrapper.text()).not.toContain('tasks.incident.open');
    expect(wrapper.text()).toContain('<b>Frozen reason</b>');
    expect(wrapper.find('b').exists()).toBe(false);
    expect(wrapper.text()).toContain('tasks.progress 1/1');
    expect(wrapper.text()).toContain('Exact evidence');
    wrapper.unmount();
  });
  it('renders arbitrary plugin text as plain content in cards and steps', () => {
    const data = report();
    data.originalPlan.tasks[0].nameText = { kind: 'plugin', key: 'custom.name', args: {}, fallback: 'Original' };
    data.displaySnapshot = { pluginId: 'arbitrary', pluginVersion: '1', defaultLocale: 'en-US', localizationHash: 'hash',
      messages: { 'en-US': { 'custom.name': '<b>Frozen task</b>' } } };
    for (const layout of ['cards', 'steps'] as const) {
      const wrapper = mount(TaskReportPanel, { props: { report: data, layout } });
      expect(wrapper.text()).toContain('<b>Frozen task</b>');
      expect(wrapper.find('b').exists()).toBe(false);
      wrapper.unmount();
    }
  });
  it("uses Host business counts while still showing child and technical cards", () => {
    const data = report();
    const base = data.originalPlan.tasks[0];
    data.originalPlan.tasks.push(
      { ...base, id: 'child', name: 'Child reward', parentId: 'mail', countsAsUnit: false },
      { ...base, id: 'login', name: 'Login', role: 'technical' },
      { ...base, id: 'not-unit', name: 'Informational', countsAsUnit: false });
    data.summary!.counts = { total: 1, succeeded: 0, skipped: 0, partial: 1 };
    for (const layout of ['cards', 'steps'] as const) {
      const wrapper = mount(TaskReportPanel, { props: { report: data, layout } });
      expect(wrapper.text()).toContain('tasks.progress 0/1');
      expect(wrapper.text()).toContain('Login');
      expect(wrapper.text()).toContain('Child reward');
      wrapper.unmount();
    }
    const preview = mount(TaskReportPanel, { props: { plan: data.originalPlan } });
    expect(preview.text()).toContain('tasks.enabled_count 0/1');
    preview.unmount();
  });
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
