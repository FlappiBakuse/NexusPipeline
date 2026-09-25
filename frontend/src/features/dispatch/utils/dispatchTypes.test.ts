import { describe, expect, it } from "vitest";
import { appendRunningLogEntries, mergeRunningRecords } from "./dispatchTypes";

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

  it("shows an empty new queue item immediately and ignores a late old log batch", () => {
    const previous = {
      id: "run-1",
      currentScriptId: "script-a",
      logSegmentId: "record-a",
      logSegmentSequence: 1,
      logEntries: [{ sequence: 10, logSegmentId: "record-a", text: "old" }],
      logTail: ["old"],
      logTruncated: true,
    };
    const [next] = mergeRunningRecords([previous], [{
      id: "run-1",
      currentScriptId: "script-a", // repeated scripts still have distinct record IDs
      logSegmentId: "record-b",
      logSegmentSequence: 2,
      logEntries: [],
      logTruncated: false,
    }]);

    expect(next.logEntries).toEqual([]);
    expect(next.logTail).toEqual([]);
    expect(next.logTruncated).toBe(false);
    expect(appendRunningLogEntries(next, [{ sequence: 11, logSegmentId: "record-a", message: "late" }]).logEntries).toEqual([]);
    expect(appendRunningLogEntries(next, [{ sequence: 12, logSegmentId: "record-b", message: "current" }]).logEntries?.map(entry => entry.message)).toEqual(["current"]);
    expect(mergeRunningRecords([next], [previous])[0]).toEqual(next);
  });
});
