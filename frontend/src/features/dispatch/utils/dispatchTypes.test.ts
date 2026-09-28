import { describe, expect, it } from "vitest";
import { appendRunningLogEntries, mergeRunningRecords, runningLogEntries } from "./dispatchTypes";

describe("dispatch realtime running records", () => {
  it.each(["different-script", "different-user", "repeated-queue-item", "next-attempt"])("clears one authoritative new segment before its first line: %s", boundary => {
    const prior = { id: "run", currentScriptId: "same-script", logSegmentId: "old-record", logSegmentSequence: 3,
      currentAttempt: 1, logEntries: [{ sequence: 1, logSegmentId: "old-record", message: boundary }], logTail: [boundary], logTruncated: true };
    const [next] = mergeRunningRecords([prior], [{ id: "run", currentScriptId: boundary === "different-script" ? "new-script" : "same-script",
      logSegmentId: boundary, logSegmentSequence: 4, currentAttempt: boundary === "next-attempt" ? 2 : 0,
      logSegment: { id: boundary, generation: 4, runRecordId: boundary === "next-attempt" ? "old-record" : boundary, attemptNumber: boundary === "next-attempt" ? 2 : 0 }, logEntries: [] }]);
    expect(runningLogEntries(next)).toEqual([]);
    expect(next.logTail).toEqual([]);
    expect(next.logTruncated).toBe(false);
    expect(next.currentAttempt).toBe(boundary === "next-attempt" ? 2 : 0);
    expect(mergeRunningRecords([next], [prior])[0]).toEqual(next);
    expect(appendRunningLogEntries(next, [{ sequence: 999, logSegmentId: "old-record", message: "late" }])).toEqual(next);
  });

  it("caps each segment independently and never carries another parallel run's tail", () => {
    const initial = { id: "a", logSegmentId: "a-one", logSegmentSequence: 1, logEntries: [] };
    const filled = appendRunningLogEntries(initial, Array.from({ length: 501 }, (_, index) => ({ sequence: index + 1, logSegmentId: "a-one", message: "old" })));
    expect(filled.logEntries).toHaveLength(500);
    expect(filled.logEntries?.[0].sequence).toBe(2);
    expect(filled.logTruncated).toBe(true);
    const other = { id: "b", logSegmentId: "b-one", logSegmentSequence: 1, logEntries: [{ sequence: 1, logSegmentId: "b-one", message: "parallel" }] };
    const [next, unchanged] = mergeRunningRecords([filled, other], [{ id: "a", logSegmentId: "a-two", logSegmentSequence: 2, logEntries: [{ sequence: 502, logSegmentId: "a-two", message: "new" }] }, { id: "b", logSegmentId: "b-one" }]);
    expect(next.logEntries?.map(entry => entry.message)).toEqual(["new"]);
    expect(next.logTruncated).toBe(false);
    expect(unchanged.logEntries).toEqual(other.logEntries);
    expect(appendRunningLogEntries(next, [{ sequence: 503, logSegmentId: "b-one", message: "wrong run" }]).logEntries).toEqual(next.logEntries);
  });

  it("keeps an omitted same-segment payload but obeys an explicit empty preparation segment", () => {
    const initial = { id: "run", logSegmentId: "one", logSegmentSequence: 1, logEntries: [{ sequence: 1, logSegmentId: "one", message: "first user" }], logTail: ["first user"] };
    const [same] = mergeRunningRecords([initial], [{ id: "run", logSegmentId: "one", currentStatus: "still running" }]);
    expect(runningLogEntries(same)).toEqual(initial.logEntries);
    const [preparation] = mergeRunningRecords([same], [{ id: "run", logSegmentId: "two", logSegmentSequence: 2, currentStatus: "preparing next user", currentAttempt: 0, logEntries: [] }]);
    expect(runningLogEntries(preparation)).toEqual([]);
    expect(preparation.logTail).toEqual([]);
  });
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
