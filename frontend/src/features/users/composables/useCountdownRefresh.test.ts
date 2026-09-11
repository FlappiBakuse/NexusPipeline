import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useCountdownRefresh } from "./useCountdownRefresh";

const BASE = Date.parse("2026-09-12T00:00:00.000Z");

describe("useCountdownRefresh", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  function setup(nowValue = BASE) {
    const clock = { now: nowValue };
    const onRefresh = vi.fn().mockResolvedValue(undefined);
    const controller = useCountdownRefresh({ onRefresh, delayMs: 1500, now: () => clock.now });
    return { onRefresh, controller, clock };
  }

  it("does not refresh before the schedule is due", async () => {
    const { onRefresh, controller } = setup();
    controller.tick([{ id: "u1", nextRunAt: new Date(BASE + 60_000).toISOString() }]);
    await vi.advanceTimersByTimeAsync(5000);
    expect(onRefresh).not.toHaveBeenCalled();
  });

  it("refreshes once after the delay when a schedule expires", async () => {
    const { onRefresh, controller } = setup();
    controller.tick([{ id: "u1", nextRunAt: new Date(BASE - 1000).toISOString() }]);
    expect(onRefresh).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1500);
    expect(onRefresh).toHaveBeenCalledTimes(1);
  });

  it("schedules a single refresh for several users expiring together", async () => {
    const { onRefresh, controller } = setup();
    controller.tick([
      { id: "u1", nextRunAt: new Date(BASE - 1000).toISOString() },
      { id: "u2", nextRunAt: new Date(BASE - 2000).toISOString() },
      { id: "u3", nextRunAt: new Date(BASE + 60_000).toISOString() },
    ]);
    await vi.advanceTimersByTimeAsync(1500);
    expect(onRefresh).toHaveBeenCalledTimes(1);
  });

  it("does not loop when the backend keeps returning the same expired timestamp", async () => {
    const { onRefresh, controller } = setup();
    const users = [{ id: "u1", nextRunAt: new Date(BASE - 1000).toISOString() }];
    controller.tick(users);
    await vi.advanceTimersByTimeAsync(1500);
    expect(onRefresh).toHaveBeenCalledTimes(1);
    // 后端仍返回同一个过期时间戳
    controller.tick(users);
    await vi.advanceTimersByTimeAsync(5000);
    expect(onRefresh).toHaveBeenCalledTimes(1);
  });

  it("refreshes again when a later schedule expires", async () => {
    const { onRefresh, controller, clock } = setup();
    controller.tick([{ id: "u1", nextRunAt: new Date(BASE - 1000).toISOString() }]);
    await vi.advanceTimersByTimeAsync(1500);
    expect(onRefresh).toHaveBeenCalledTimes(1);
    // 刷新后后端返回下一个计划；时间推进到该计划到期，应再次刷新。
    clock.now = BASE + 120_000;
    controller.tick([{ id: "u1", nextRunAt: new Date(BASE + 60_000).toISOString() }]);
    await vi.advanceTimersByTimeAsync(1500);
    expect(onRefresh).toHaveBeenCalledTimes(2);
  });

  it("does not refresh after dispose", async () => {
    const { onRefresh, controller } = setup();
    controller.tick([{ id: "u1", nextRunAt: new Date(BASE - 1000).toISOString() }]);
    controller.dispose();
    await vi.advanceTimersByTimeAsync(5000);
    expect(onRefresh).not.toHaveBeenCalled();
  });

  it("ignores an in-flight refresh window until it settles", async () => {
    let release: () => void = () => {};
    const onRefresh = vi.fn().mockImplementation(() => new Promise<void>(resolve => { release = resolve; }));
    const controller = useCountdownRefresh({ onRefresh, delayMs: 1500, now: () => BASE });
    const users = [{ id: "u1", nextRunAt: new Date(BASE - 1000).toISOString() }];
    controller.tick(users);
    await vi.advanceTimersByTimeAsync(1500);
    controller.tick(users);
    await vi.advanceTimersByTimeAsync(1500);
    expect(onRefresh).toHaveBeenCalledTimes(1);
    release();
    await vi.advanceTimersByTimeAsync(0);
    expect(onRefresh).toHaveBeenCalledTimes(1);
  });
});
