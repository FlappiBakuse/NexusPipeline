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
    { date: "2026-09-10", totalCount: 0, statusCounts: {}, totalDurationMs: 0 },
    { date: "2026-09-11", totalCount: 1, statusCounts: { success: 1 }, totalDurationMs: 1000 },
    { date: "2026-09-12", totalCount: 2, statusCounts: { partial: 1, failed: 1 }, totalDurationMs: 2000 },
    { date: "2026-09-13", totalCount: 2, statusCounts: { success: 1, failed: 1 }, totalDurationMs: 2000 },
    { date: "2026-09-14", totalCount: 2, statusCounts: { success: 1, partial: 1 }, totalDurationMs: 2000 },
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
  it("renders the two cards, donut distribution, and seven-day stacked trend", () => {
    const wrapper = mountSummary();

    expect(wrapper.findAll(".dashboard-history-card")).toHaveLength(2);
    expect(wrapper.findAll(".dashboard-history-status")).toHaveLength(5);
    expect(wrapper.get("[data-status='success']").text()).toContain("7");
    expect(wrapper.get("[data-status='partial']").text()).toContain("2");
    expect(wrapper.get("[data-status='failed']").text()).toContain("3");
    expect(wrapper.get("[data-status='cancelled']").text()).toContain("0");
    expect(wrapper.get("[data-status='cancelled']").classes()).toContain("is-muted");
    expect(wrapper.get(".dashboard-history-donut").attributes("aria-label")).toContain("12");
    expect(wrapper.get("[data-status='success']").attributes("aria-label")).toContain("7");

    const trend = wrapper.get("[data-testid='dashboard-history-trend']");
    expect(wrapper.findAll(".dashboard-history-trend-day")).toHaveLength(7);
    expect(trend.text()).not.toContain("2026");
    expect(wrapper.find(".dashboard-history-trend-day").attributes("data-tooltip")).toContain("成功");
    expect(wrapper.find(".dashboard-history-trend-day").attributes("aria-label")).toContain("其他");
    expect(wrapper.findAll(".dashboard-history-trend-track")).toHaveLength(7);
    expect(wrapper.get("[data-testid='dashboard-history-summary-performance']").text()).toContain("58.3");
  });

  it("shows cancelled and skipped values while preserving zero-value legend rows", () => {
    const wrapper = mountSummary({
      ...baseSummary,
      statusCounts: { ...baseSummary.statusCounts, cancelled: 1, skipped: 2 },
    });

    expect(wrapper.get("[data-status='cancelled']").text()).toContain("1");
    expect(wrapper.get("[data-status='skipped']").text()).toContain("2");
    expect(wrapper.get("[data-status='cancelled']").classes()).not.toContain("is-muted");
  });
});
