import { describe, expect, it } from "vitest";
import { durationClock, queueCountdown } from "./queueUtils";

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
