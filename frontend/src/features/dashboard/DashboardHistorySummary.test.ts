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
  daily: [
    { date: "2026-09-08", totalCount: 2, statusCounts: { success: 1, failed: 1 }, totalDurationMs: 1000 },
    { date: "2026-09-09", totalCount: 3, statusCounts: { success: 3 }, totalDurationMs: 2000 },
  ],
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
  it("renders the two cards, seven-day trend, and complete status distribution", () => {
    const wrapper = mountSummary();

    expect(wrapper.findAll(".dashboard-history-card")).toHaveLength(2);
    expect(wrapper.get("[data-testid='dashboard-history-summary-total']").text()).toContain("12");
    expect(wrapper.get("[data-status='success']").text()).toContain("7");
    expect(wrapper.get("[data-status='partial']").text()).toContain("2");
    expect(wrapper.get("[data-status='failed']").text()).toContain("3");
    expect(wrapper.get("[data-status='cancelled']").text()).toContain("0");
    expect(wrapper.get("[data-status='skipped']").text()).toContain("0");
    expect(wrapper.get("[data-testid='dashboard-history-trend']").text()).toContain("2026");
    expect(wrapper.findAll(".dashboard-history-trend-day")).toHaveLength(2);
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
