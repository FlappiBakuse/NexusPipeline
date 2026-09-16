import { describe, expect, it } from "vitest";
import { mergeRunningRecords } from "./dispatchTypes";

describe("dispatch realtime running records", () => {
  it("keeps run log entries when a later host summary has no log payload", () => {
    const afterRunLog = {
      id: "run-1",
      targetName: "演示脚本",
      status: "running",
      logEntries: [
        { sequence: 1, level: "info", text: "one" },
        { sequence: 2, level: "info", text: "two" },
        { sequence: 3, level: "warn", text: "three" },
      ],
    };

    const afterHostStatus = mergeRunningRecords(
      [afterRunLog],
      [{ id: "run-1", targetName: "演示脚本", status: "running" }],
    );

    expect(afterHostStatus[0].logEntries?.map(entry => entry.sequence)).toEqual([1, 2, 3]);
    expect(afterHostStatus[0].logTruncated).toBe(false);
  });
});
