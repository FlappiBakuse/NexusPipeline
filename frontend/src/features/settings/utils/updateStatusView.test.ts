import { describe, expect, it } from "vitest";
import { updateStatusView } from "./updateStatusView";

describe("updateStatusView", () => {
  it("shows the backup warning only while the update is ready", () => {
    expect(updateStatusView("ready")).toEqual({
      state: "ready",
      readyConfirmKey: "settings.update.ready_confirm",
      showBackupWarning: true,
    });
    expect(updateStatusView("downloading").showBackupWarning).toBe(false);
    expect(updateStatusView("idle").showBackupWarning).toBe(false);
    expect(updateStatusView("applying").showBackupWarning).toBe(false);
  });

  it("normalizes an absent state to idle", () => {
    expect(updateStatusView(undefined)).toMatchObject({ state: "idle", showBackupWarning: false });
    expect(updateStatusView("")).toMatchObject({ state: "idle", showBackupWarning: false });
  });
});
