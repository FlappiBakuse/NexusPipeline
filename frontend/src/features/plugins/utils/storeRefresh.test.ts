import { describe, expect, it, vi } from "vitest";
import { refreshStoreRepository } from "./storeRefresh";

describe("refreshStoreRepository", () => {
  it("refreshes the host catalog before reloading the store list", async () => {
    const order: string[] = [];
    await refreshStoreRepository({
      refreshRepository: vi.fn(async () => { order.push("refresh"); }),
      reloadStore: vi.fn(async () => { order.push("reload"); }),
    });
    expect(order).toEqual(["refresh", "reload"]);
  });

  it("propagates a refresh failure without reloading", async () => {
    const reloadStore = vi.fn().mockResolvedValue(undefined);
    await expect(
      refreshStoreRepository({
        refreshRepository: vi.fn().mockRejectedValue(new Error("offline")),
        reloadStore,
      }),
    ).rejects.toThrow("offline");
    expect(reloadStore).not.toHaveBeenCalled();
  });

  it("propagates a reload failure", async () => {
    await expect(
      refreshStoreRepository({
        refreshRepository: vi.fn().mockResolvedValue(undefined),
        reloadStore: vi.fn().mockRejectedValue(new Error("store failed")),
      }),
    ).rejects.toThrow("store failed");
  });
});
