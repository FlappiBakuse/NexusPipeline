export type RangeAnchor = "from" | "to";

export interface DateRangeDraft {
  from: string;
  to: string;
}

export interface CalendarDay {
  value: string;
  empty: boolean;
  disabled: boolean;
  inRange: boolean;
  start: boolean;
  end: boolean;
}

const DATE_KEY = /^(\d{4})-(\d{2})-(\d{2})$/u;
const MONTH_KEY = /^(\d{4})-(\d{2})$/u;

function pad(value: number) {
  return String(value).padStart(2, "0");
}

function createLocalDate(year: number, month: number, day: number) {
  const date = new Date(0);
  date.setHours(0, 0, 0, 0);
  date.setFullYear(year, month - 1, day);
  return date;
}

export function dateKey(date: Date = new Date()) {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

export function parseDateKey(value: unknown) {
  const match = DATE_KEY.exec(String(value || ""));
  if (!match) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  if (year < 1 || month < 1 || month > 12 || day < 1) return null;
  const date = createLocalDate(year, month, day);
  if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day) return null;
  return { year, month, day };
}

export function normalizeDateKey(value: unknown, fallback = "") {
  return parseDateKey(value) ? String(value) : fallback;
}

export function monthKey(value: unknown, fallback = dateKey().slice(0, 7)) {
  const match = MONTH_KEY.exec(String(value || ""));
  if (!match) return fallback;
  const year = Number(match[1]);
  const month = Number(match[2]);
  return year >= 1 && month >= 1 && month <= 12 ? `${match[1]}-${match[2]}` : fallback;
}

export function shiftMonth(value: string, offset: number) {
  const normalized = monthKey(value);
  const [year, month] = normalized.split("-").map(Number);
  const date = createLocalDate(year, month + offset, 1);
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}`;
}

export function daysInMonth(value: string) {
  const normalized = monthKey(value);
  const [year, month] = normalized.split("-").map(Number);
  return createLocalDate(year, month + 1, 0).getDate();
}

export function normalizeRange(from: string, to: string): DateRangeDraft {
  const safeFrom = normalizeDateKey(from);
  const safeTo = normalizeDateKey(to);
  if (!safeFrom || !safeTo || safeFrom <= safeTo) return { from: safeFrom, to: safeTo };
  return { from: safeTo, to: safeFrom };
}

export function chooseRangeDate(
  draft: DateRangeDraft,
  anchor: RangeAnchor,
  value: string,
  maxDate: string,
) {
  if (!parseDateKey(value) || (parseDateKey(maxDate) && value > maxDate)) {
    return { draft: { ...draft }, anchor };
  }

  if (anchor === "from") {
    return {
      draft: { from: value, to: draft.to && draft.to < value ? value : draft.to },
      anchor: "to" as const,
    };
  }

  if (draft.from && value < draft.from) {
    return { draft: { from: value, to: draft.from }, anchor: "from" as const };
  }
  return { draft: { from: draft.from || value, to: value }, anchor: "from" as const };
}

export function calendarDays(value: string, draft: DateRangeDraft, maxDate: string): CalendarDay[] {
  const normalized = monthKey(value);
  const [year, month] = normalized.split("-").map(Number);
  const first = createLocalDate(year, month, 1);
  const count = daysInMonth(normalized);
  const days: CalendarDay[] = [];
  for (let index = 0; index < first.getDay(); index += 1) {
    days.push({ value: "", empty: true, disabled: true, inRange: false, start: false, end: false });
  }
  for (let day = 1; day <= count; day += 1) {
    const current = `${year}-${pad(month)}-${pad(day)}`;
    days.push({
      value: current,
      empty: false,
      disabled: Boolean(maxDate && current > maxDate),
      inRange: Boolean(draft.from && draft.to && current > draft.from && current < draft.to),
      start: current === draft.from,
      end: current === draft.to,
    });
  }
  return days;
}

export function canMoveToNextMonth(visibleEndMonth: string, maxDate: string) {
  return monthKey(visibleEndMonth) < monthKey(maxDate);
}

