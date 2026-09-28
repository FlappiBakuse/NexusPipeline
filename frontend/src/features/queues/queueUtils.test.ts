import { describe, expect, it } from "vitest";
import { durationClock, queueCountdown, reorderTimeSetItems, timeSetKey } from "./queueUtils";

describe("queue countdown formatting", () => {
  it("formats hours, minutes, and seconds consistently", () => {
    expect(durationClock(3723)).toBe("01:02:03");
    expect(durationClock(-2)).toBe("00:00:00");
  });

  it("distinguishes waiting, active countdown, and due schedules", () => {
    const now = Date.parse("2026-09-11T00:00:00.000Z");
    expect(queueCountdown(undefined, now)).toEqual({ kind: "waiting" });
    expect(queueCountdown("2026-09-11T00:00:02.500Z", now)).toEqual({ kind: "countdown", duration: "00:00:02" });
    expect(queueCountdown("2026-09-10T23:59:59.000Z", now)).toEqual({ kind: "about" });
  });
});

describe("schedule reorder", () => {
  const saved = { id: "saved-schedule-a", enabled: true, days: [1, 2], time: "12:30" };
  const other = { id: "saved-schedule-b", enabled: false, days: [3], time: "13:30" };
  it("moves saved schedule IDs and preserves all schedule settings", () => {
    expect(reorderTimeSetItems([saved, other], [other.id, saved.id])).toEqual([other, saved]);
  });
  it("moves an unsaved schedule using the same key as its card", () => {
    const unsaved = { enabled: true, days: [5], time: "18:30" };
    expect(reorderTimeSetItems([saved, unsaved], [timeSetKey(unsaved, 1), saved.id])).toEqual([unsaved, saved]);
  });
  it("retains every schedule when order contains unknown, duplicate or missing IDs", () => {
    const current = [saved, other];
    for (const ids of [[saved.id, "unknown"], [saved.id, saved.id], [other.id]])
      expect(reorderTimeSetItems(current, ids)).toBe(current);
  });
});
