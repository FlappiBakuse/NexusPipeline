import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import DashboardHistorySummary from "./DashboardHistorySummary.vue";
import type { HistorySummary } from "../history/utils/historyTypes";

const baseSummary: HistorySummary = {
  totalCount: 12,
  statusCounts: { success: 7, partial: 2, failed: 3, cancelled: 0, skipped: 0 },
  totalDurationMs: 12_000,
  averageDurationMs: 1_000,
  successRate: 58.3,
  daily: [],
};

function mountSummary(summary = baseSummary) {
  return mount(DashboardHistorySummary, {
    props: {
      summary,
      loading: false,
      error: "",
      from: "2026-09-08",
      to: "2026-09-14",
    },
  });
}

describe("DashboardHistorySummary", () => {
  it("renders the two dashboard cards and hides zero-value optional statuses", () => {
    const wrapper = mountSummary();

    expect(wrapper.findAll(".dashboard-history-card")).toHaveLength(2);
    expect(wrapper.get("[data-testid='dashboard-history-summary-total']").text()).toContain("12");
    expect(wrapper.get("[data-status='success']").text()).toContain("7");
    expect(wrapper.get("[data-status='partial']").text()).toContain("2");
    expect(wrapper.get("[data-status='failed']").text()).toContain("3");
    expect(wrapper.find("[data-status='cancelled']").exists()).toBe(false);
    expect(wrapper.find("[data-status='skipped']").exists()).toBe(false);
    expect(wrapper.get("[data-testid='dashboard-history-summary-performance']").text()).toContain("58.3");
  });

  it("shows cancelled and skipped only when the backend reports them", () => {
    const wrapper = mountSummary({
      ...baseSummary,
      statusCounts: { ...baseSummary.statusCounts, cancelled: 1, skipped: 2 },
    });

    expect(wrapper.get("[data-status='cancelled']").text()).toContain("1");
    expect(wrapper.get("[data-status='skipped']").text()).toContain("2");
  });
});
