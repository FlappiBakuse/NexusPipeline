import { arrayValue, integerValue, objectValue, stringValue } from '../control/client';
import type { RecordSummary, RecordTotals } from './dashboard-types';

export function dashboardSummary(value: unknown): RecordSummary {
  const row = objectValue(value);
  return {
    totals: totals(row.totals),
    days: arrayValue(row.days, (value) => {
      const day = objectValue(value);
      return { date: stringValue(day.date), totals: totals(day.totals) };
    })
  };
}

export function sevenDays(): { dates: string[]; query: string } {
  const today = new Date(),
    offset = -today.getTimezoneOffset();
  const end = new Date(today.getFullYear(), today.getMonth(), today.getDate() + 1);
  const start = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 6);
  const days = Array.from({ length: 7 }, (_, index) => {
    const date = new Date(start.getFullYear(), start.getMonth(), start.getDate() + index);
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
  });
  const boundary = (date: Date): string =>
    new Date(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()) - offset * 60000).toISOString();
  const query = new URLSearchParams({
    endedFromInclusive: boundary(start),
    endedUntilExclusive: boundary(end),
    offsetMinutes: String(offset)
  });
  return { dates: days, query: query.toString() };
}
export function totals(value: unknown): RecordTotals {
  const row = objectValue(value);
  return {
    runs: integerValue(row.runs),
    succeeded: integerValue(row.succeeded),
    failed: integerValue(row.failed),
    cancelled: integerValue(row.cancelled),
    unverified: integerValue(row.unverified),
    measuredCount: integerValue(row.measuredCount),
    measuredMilliseconds: integerValue(row.measuredMilliseconds),
    lowerBoundCount: integerValue(row.lowerBoundCount),
    unknownDurationCount: integerValue(row.unknownDurationCount)
  };
}
