import { describe, expect, it } from "vitest";
import { formatDurationMs, formatHistoryShortDate } from "./historyFormat";

describe("historyFormat", () => {
  it("formats completed durations across useful time units", () => {
    expect(formatDurationMs(null)).toBe("-");
    expect(formatDurationMs(0)).toContain("0");
    expect(formatDurationMs(1500)).toContain("1.5");
    expect(formatDurationMs(90_000)).toContain("1.5");
    expect(formatDurationMs(3_600_000)).toContain("1");
  });

  it("rejects invalid or negative durations", () => {
    expect(formatDurationMs(-1)).toBe("-");
    expect(formatDurationMs(Number.NaN)).toBe("-");
  });

  it("formats dashboard dates without the year", () => {
    expect(formatHistoryShortDate("2026-09-08")).not.toContain("2026");
    expect(formatHistoryShortDate("invalid-date")).toBe("invalid-date");
  });
});
