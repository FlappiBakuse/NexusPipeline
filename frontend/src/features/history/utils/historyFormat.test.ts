import { describe, expect, it } from "vitest";
import { formatDurationMs } from "./historyFormat";

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
});
