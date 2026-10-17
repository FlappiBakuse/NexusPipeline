import { api } from '../../platform/api';
import { historyTodayValue } from './utils/historyFormat';
import type { HistoryDate, HistoryUser, HistoryRecord } from './utils/historyTypes';

export function initialHistoryRange(now = new Date()) {
  const start = new Date(now); start.setDate(start.getDate() - 29);
  return { from: historyTodayValue(start), to: historyTodayValue(now) };
}
export async function loadHistoryPage(signal?: AbortSignal, recordId?: string, mobile = window.innerWidth <= 820) {
  if (recordId) {
    const data = await api<{ record?: HistoryRecord }>('GET', `/api/history/detail?id=${encodeURIComponent(recordId)}&metadata=true`, undefined, signal);
    if (!data.record) throw new Error('record_deleted');
    const day = (data.record.startTime || '').slice(0, 10);
    return { from: day, to: day, dates: [{ date: day, count: 1 }] as HistoryDate[], users: [] as HistoryUser[], unsupportedCount: 0, record: data.record };
  }
  const range = initialHistoryRange();
  const data = await api<{ dates?: HistoryDate[]; formatStatus?: { unsupportedCount?: number } }>('GET', `/api/history/dates?from=${range.from}&to=${range.to}`, undefined, signal);
  const dates = Array.isArray(data.dates) ? data.dates : [];
  const users = !mobile && dates.length
    ? await api<{ users?: HistoryUser[]; formatStatus?: { unsupportedCount?: number } }>('GET', `/api/history/users?date=${encodeURIComponent(dates[0].date)}`, undefined, signal) : null;
  return { ...range, dates, users: users?.users || [], unsupportedCount: Math.max(data.formatStatus?.unsupportedCount || 0, users?.formatStatus?.unsupportedCount || 0), record: undefined };
}
