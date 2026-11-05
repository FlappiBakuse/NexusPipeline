import { arrayValue, boundedInteger, dateValue, enumValue, integerValue, objectValue, stringValue } from './client';
export interface AccountSummary {
  userId: string;
  summaryReason: 'no_bindings' | 'not_run' | 'record_deleted' | 'no_task_report' | 'latest';
  summaryTone: 'bad' | 'warn' | 'muted' | 'ok';
  runId: string | null;
  activeCount: number;
  nextRunAt: string | null;
  queueId: string | null;
}
export interface AccountSummarySnapshot {
  catalogRevision: number;
  progressRevision: number;
  observedAt: string;
  items: AccountSummary[];
}
export function accountSummaries(value: unknown, users: readonly string[]): AccountSummarySnapshot {
  const row = objectValue(value);
  const items = arrayValue(row.items, value => {
    const item = objectValue(value);
    const result: AccountSummary = {
      userId: stringValue(item.userId),
      summaryReason: enumValue(item.summaryReason, ['no_bindings', 'not_run', 'record_deleted', 'no_task_report', 'latest']),
      summaryTone: enumValue(item.summaryTone, ['bad', 'warn', 'muted', 'ok']),
      runId: item.runId === null ? null : stringValue(item.runId),
      activeCount: boundedInteger(item.activeCount, 0, 10000),
      nextRunAt: item.nextRunAt === null ? null : dateValue(item.nextRunAt),
      queueId: item.queueId === null ? null : stringValue(item.queueId)
    };
    if (!users.includes(result.userId) || (result.nextRunAt === null) !== (result.queueId === null) ||
        result.summaryReason === 'record_deleted' && result.runId !== null)
      throw new TypeError('account_summary_unbound');
    return result;
  });
  if (items.length !== users.length || new Set(items.map(item => item.userId)).size !== users.length)
    throw new TypeError('account_summary_identity_invalid');
  return { catalogRevision: integerValue(row.catalogRevision), progressRevision: integerValue(row.progressRevision),
    observedAt: dateValue(row.observedAt), items };
}
