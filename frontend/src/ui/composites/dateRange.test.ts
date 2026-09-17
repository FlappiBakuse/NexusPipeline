import { describe, expect, it } from "vitest";
import {
  calendarDays,
  chooseRangeDate,
  daysInMonth,
  normalizeRange,
  shiftMonth,
} from "./dateRange";

describe("date range primitives", () => {
  it("handles leap years, month ends and year transitions with local date keys", () => {
    expect(daysInMonth("2024-02")).toBe(29);
    expect(daysInMonth("2025-02")).toBe(28);
    expect(shiftMonth("2025-12", 1)).toBe("2026-01");
    expect(shiftMonth("2026-01", -1)).toBe("2025-12");
  });

  it("normalizes a reverse range without changing a same-day range", () => {
    expect(normalizeRange("2026-09-12", "2026-08-14")).toEqual({ from: "2026-08-14", to: "2026-09-12" });
    expect(normalizeRange("2026-08-14", "2026-08-14")).toEqual({ from: "2026-08-14", to: "2026-08-14" });
  });

  it("keeps future dates disabled and selects a reverse endpoint deterministically", () => {
    const days = calendarDays("2026-09", { from: "2026-09-02", to: "2026-09-08" }, "2026-09-17");
    expect(days.find(day => day.value === "2026-09-08")?.end).toBe(true);
    expect(days.find(day => day.value === "2026-09-18")?.disabled).toBe(true);

    const selected = chooseRangeDate(
      { from: "2026-09-12", to: "2026-09-12" },
      "to",
      "2026-09-05",
      "2026-09-17",
    );
    expect(selected).toEqual({ draft: { from: "2026-09-05", to: "2026-09-12" }, anchor: "from" });
    expect(chooseRangeDate(selected.draft, selected.anchor, "2026-09-18", "2026-09-17")).toEqual({
      draft: selected.draft,
      anchor: "from",
    });
  });
});
