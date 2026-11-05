export function localDate(date: Date): string {
  return [date.getFullYear(), String(date.getMonth() + 1).padStart(2, '0'), String(date.getDate()).padStart(2, '0')].join('-');
}
export function timeRange(start: string, end: string): { from: string; until: string } {
  const last = new Date(end + 'T00:00:00');
  last.setDate(last.getDate() + 1);
  return { from: new Date(start + 'T00:00:00').toISOString(), until: last.toISOString() };
}
