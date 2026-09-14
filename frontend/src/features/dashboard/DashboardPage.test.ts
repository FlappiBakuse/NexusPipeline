import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const requestPaths: string[] = [];
const apiMock = vi.fn();

vi.mock("../../platform/shell", () => ({ setTopbarTitle: vi.fn() }));
vi.mock("@bridge/index", () => ({
  renderPluginSlot: vi.fn(),
  disposePluginSlot: vi.fn(),
}));
vi.mock("../../platform/api", () => ({
  api: (...args: unknown[]) => apiMock(...args),
  isAbortError: () => false,
}));

import DashboardPage from "./DashboardPage.vue";

const summary = {
  totalCount: 4,
  statusCounts: { success: 3, partial: 0, failed: 1, cancelled: 0, skipped: 0 },
  totalDurationMs: 4000,
  averageDurationMs: 1000,
  successRate: 75,
  daily: [],
};

describe("DashboardPage history summary polling", () => {
  let wrapper: VueWrapper | null = null;

  beforeEach(() => {
    vi.useFakeTimers();
    requestPaths.length = 0;
    apiMock.mockReset();
    apiMock.mockImplementation(async (_method: string, path: string) => {
      requestPaths.push(path);
      if (path.startsWith("/api/history/summary")) return summary;
      return { version: "0.16.0", running: [], plugins: [] };
    });
  });

  afterEach(() => {
    wrapper?.unmount();
    wrapper = null;
    vi.useRealTimers();
  });

  it("loads summary once and refreshes it on its own 60-second cadence", async () => {
    wrapper = mount(DashboardPage, { attachTo: document.body });
    await flushPromises();

    expect(wrapper.get("[data-testid='dashboard-history-summary-total']").text()).toContain("4");
    expect(requestPaths.filter(path => path.startsWith("/api/status"))).toHaveLength(1);
    expect(requestPaths.filter(path => path.startsWith("/api/history/summary"))).toHaveLength(1);

    await vi.advanceTimersByTimeAsync(60_000);

    expect(requestPaths.filter(path => path.startsWith("/api/history/summary"))).toHaveLength(2);
    expect(requestPaths.filter(path => path.startsWith("/api/status"))).toHaveLength(21);
  });
});
