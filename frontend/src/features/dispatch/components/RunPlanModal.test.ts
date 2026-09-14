import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";

vi.mock("../../../platform/i18n", () => ({
  t: (key: string, args: Record<string, unknown> = {}, fallback = "") => {
    const messages: Record<string, string> = {
      "common.task": "Task",
      "common.user": "User",
      "common.completion_action": "Completion action",
      "common.unit.tasks": `${args.count ?? 0} tasks`,
      "common.sleep": "Sleep",
      "dispatch.summary.users": `${args.count ?? 0} users`,
      "dispatch.user_eligibility": "User eligibility",
      "dispatch.plan.successful_today": `${args.successful ?? 0}/${args.maximum ?? "∞"}`,
      "dispatch.ready": "Ready",
      "dispatch.will_skip": "Will skip",
      "dispatch.blocked": "Blocked",
      "dispatch.reason.ready": "Ready",
      "dispatch.reason.daily_success_cap": "Daily cap reached",
      "dispatch.notice": "Notice",
      "common.close": "Close",
      "dispatch.run_plan_check": "Run plan check",
      "dispatch.target": "Target",
      "dispatch.ready_to_run": "Ready to run",
      "dispatch.queue_class_standard": "Standard queue",
      "common.task_list": "Task list",
      "dispatch.users": "Users",
      "common.status": "Status",
      "dispatch.successful_today": "Successful today",
      "common.reason": "Reason",
      "dispatch.unnamed_user": "Unnamed user",
    };
    return messages[key] || fallback || key;
  },
}));

import RunPlanModal from "./RunPlanModal.vue";

const plan = {
  targetName: "夜间队列",
  admissible: true,
  totalTasks: 3,
  queueClass: "standard",
  completionAction: "sleep",
  tasks: [
    { taskId: "task-1", scriptName: "同步一", userCount: 2 },
    { taskId: "task-2", scriptName: "同步二", userCount: 1 },
  ],
  users: [
    { userName: "alice", status: "ready", successfulRunsToday: 3, maxSuccessfulRunsPerDay: 5, reasonCode: "ready" },
    { userName: "bob", status: "skipped", successfulRunsToday: 3, maxSuccessfulRunsPerDay: -1, reasonCode: "daily_success_cap", reasonArgs: { successfulRuns: 3, maximum: 3 } },
  ],
  warnings: [{ code: "config_path_missing", args: {} }],
};

describe("RunPlanModal", () => {
  it("keeps the three summary metrics stable and exposes tasks as a list", () => {
    const wrapper = mount(RunPlanModal, { props: { plan } });

    expect(wrapper.get("[data-testid='execution-plan-summary']").findAll("[data-metric]")).toHaveLength(3);
    expect(wrapper.get("[data-metric='tasks']").text()).toContain("3");
    expect(wrapper.get("[data-metric='users']").text()).toContain("2");
    expect(wrapper.get("[data-metric='completion-action']").text()).toContain("Sleep");
    expect(wrapper.get("[role='list']").findAll("[role='listitem']")).toHaveLength(2);
    expect(wrapper.find(".execution-plan-task-header").exists()).toBe(false);
  });

  it("keeps user status, today limits, and warnings observable", () => {
    const wrapper = mount(RunPlanModal, { props: { plan } });
    const users = wrapper.get("[role='table']").findAll("[role='row']");

    expect(users[1].text()).toContain("3/5");
    expect(users[2].text()).toContain("3/∞");
    expect(wrapper.find("[data-testid='execution-plan-warnings']").exists()).toBe(true);
  });
});
