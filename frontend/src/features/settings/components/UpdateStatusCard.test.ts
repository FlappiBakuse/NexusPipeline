import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import UpdateStatusCard from "./UpdateStatusCard.vue";

const fixture = vi.hoisted(() => ({ api: vi.fn() }));
vi.mock("../../../platform/api", () => ({ api: fixture.api, isAbortError: () => false }));
vi.mock("../../../platform/i18n", () => ({ t: (key: string, values?: Record<string, unknown>) => key + JSON.stringify(values || {}) }));
vi.mock("../../../platform/toast", () => ({ toast: vi.fn() }));
let wrapper: VueWrapper | undefined;
const start = new Date("2026-10-12T00:00:00Z");
const idle = () => ({ state: "idle", current: "0.17.1", automation: { checkEnabled: true, nextCheckAt: new Date(start.getTime() + 5000).toISOString() } });
async function setup() { wrapper = mount(UpdateStatusCard); await flushPromises(); }
beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(start); fixture.api.mockReset(); fixture.api.mockResolvedValue(idle()); });
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); });

describe("update status synchronization", () => {
  it("observes the first automatic check and its result without a manual check", async () => {
    fixture.api.mockResolvedValueOnce(idle()).mockResolvedValueOnce({ ...idle(), state: "checking" }).mockResolvedValue({ ...idle(), available: true, latest: "0.17.2", automation: { checkEnabled: true, nextCheckAt: new Date(start.getTime() + 43206000).toISOString() } });
    await setup();
    await vi.advanceTimersByTimeAsync(4999); expect(fixture.api).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1); await flushPromises(); expect(fixture.api).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(1000); await flushPromises(); expect(wrapper!.text()).toContain("0.17.2");
    expect(fixture.api.mock.calls.every(call => call[0] === "GET")).toBe(true);
    await vi.advanceTimersByTimeAsync(59999); expect(fixture.api).toHaveBeenCalledTimes(3);
  });
  it("reschedules after settings reload and keeps polling an overdue check", async () => {
    fixture.api.mockResolvedValueOnce({ state: "idle", automation: { checkEnabled: false } });
    await setup(); await vi.advanceTimersByTimeAsync(3000); expect(fixture.api).toHaveBeenCalledTimes(1);
    await (wrapper!.vm as unknown as { reload: () => Promise<void> }).reload();
    await vi.advanceTimersByTimeAsync(2000); await flushPromises(); expect(fixture.api).toHaveBeenCalledTimes(3);
    await vi.advanceTimersByTimeAsync(1000); await flushPromises(); expect(fixture.api).toHaveBeenCalledTimes(4);
  });
  it("manual checking does not move the automatic deadline", async () => {
    await setup();
    const check = wrapper!.findAll("button").find(button => button.text().includes("common.check_for_updates"))!;
    await check.trigger("click"); await flushPromises();
    await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    expect(fixture.api.mock.calls.map(call => call[0])).toEqual(["GET", "POST", "GET"]);
  });
  it("backs off failed requests and stops polling after authentication failure", async () => {
    fixture.api.mockRejectedValueOnce(new Error("offline")).mockRejectedValueOnce(new Error("offline")).mockRejectedValueOnce({ status: 401 });
    await setup(); await vi.advanceTimersByTimeAsync(2499); expect(fixture.api).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1); await flushPromises(); expect(fixture.api).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(5000); await flushPromises(); expect(fixture.api).toHaveBeenCalledTimes(3);
    await vi.advanceTimersByTimeAsync(60000); expect(fixture.api).toHaveBeenCalledTimes(3);
  });
  it("polls applying updates with periodic checks disabled and releases its timer on unmount", async () => {
    fixture.api.mockResolvedValue({ state: "applying", automation: { checkEnabled: false } });
    await setup(); await vi.advanceTimersByTimeAsync(1000); await flushPromises(); expect(fixture.api).toHaveBeenCalledTimes(2);
    wrapper!.unmount(); wrapper = undefined; await vi.advanceTimersByTimeAsync(60000); expect(fixture.api).toHaveBeenCalledTimes(2);
  });
});
