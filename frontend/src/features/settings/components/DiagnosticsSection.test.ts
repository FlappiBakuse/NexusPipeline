import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";

const apiMock = vi.fn();

vi.mock("../../../platform/api", () => ({
  api: (...args: unknown[]) => apiMock(...args),
  isAbortError: () => false,
}));
vi.mock("../../../platform/toast", () => ({ toast: vi.fn() }));

import DiagnosticsSection from "./DiagnosticsSection.vue";

describe("DiagnosticsSection", () => {
  beforeEach(() => {
    apiMock.mockReset();
    apiMock.mockResolvedValue({
      overallStatus: "warn",
      hostVersion: "0.16.1",
      checks: [
        { id: "host.version", category: "host", status: "pass", summaryCode: "diagnostics.host.summary.pass", detailCode: "diagnostics.host.detail.version" },
        { id: "network.web", category: "network", status: "warn", summaryCode: "diagnostics.web.summary.warn", detailCode: "diagnostics.web.detail.not_listening", remediationCode: "diagnostics.web.remediation.start" },
        { id: "host.recovery", category: "host", status: "skipped", summaryCode: "diagnostics.update.summary.warn", detailCode: "diagnostics.update.detail.artifacts" },
        { id: "missing.category", status: "fail", summaryCode: "diagnostics.update.summary.fail", detailCode: "diagnostics.update.detail.artifacts" },
      ],
    });
  });

  it("groups checks in first-seen category order and shows group counts", async () => {
    const wrapper = mount(DiagnosticsSection);
    await flushPromises();

    const groups = wrapper.findAll("[data-diagnostic-category]");
    expect(groups).toHaveLength(3);
    expect(groups.map(group => group.attributes("data-diagnostic-category"))).toEqual(["host", "network", "uncategorized"]);
    expect(groups[0].find(".diagnostics-group-heading").text()).toContain("2");
    expect(groups[1].find(".diagnostics-group-heading").text()).toContain("1");
  });

  it("keeps identifiers accessible and limits detail/remediation to attention states", async () => {
    const wrapper = mount(DiagnosticsSection);
    await flushPromises();

    const rows = wrapper.findAll(".diagnostic-row");
    expect(rows[0].find(".sr-only").text()).toContain("host.version");
    expect(rows[0].find(".diagnostic-check-title").attributes("title")).toBe("host.version");
    expect(rows[0].find(".diagnostic-check-detail").exists()).toBe(false);
    const warningRow = wrapper.get("[data-diagnostic-status='warn']");
    expect(warningRow.find(".diagnostic-check-detail").exists()).toBe(true);
    expect(warningRow.find(".diagnostic-check-remediation").exists()).toBe(true);
    expect(wrapper.get("[data-diagnostic-status='skipped']").find(".diagnostic-check-detail").exists()).toBe(false);
  });
});
