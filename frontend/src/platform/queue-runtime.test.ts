import { describe, expect, it } from "vitest";
import { enterPage, state } from "./page-state";
import { queueRuntimeLimits } from "./queue-runtime";

describe("queue runtime limits", () => {
  it("projects the host limit snapshot loaded during shell bootstrap", () => {
    state.limits = { maxQueues: 8, maxTimeSetsPerQueue: 4, maxQueueTotalUsers: 40 };
    expect(queueRuntimeLimits()).toEqual({ maxQueues: 8, maxTimeSetsPerQueue: 4, maxQueueTotalUsers: 40 });
  });

  it("falls back to an empty limit set when the snapshot is unavailable", () => {
    state.limits = undefined;
    expect(queueRuntimeLimits()).toEqual({});
  });

  it("keeps limits across page transitions", () => {
    state.limits = { maxQueues: 3 };
    enterPage("queues");
    expect(queueRuntimeLimits().maxQueues).toBe(3);
    state.limits = undefined;
  });
});
