import { arrayValue, boundedInteger, dateValue, enumValue, integerValue, objectValue, stringValue, uuidValue } from './client';

export interface AttemptFacts {
  runId: string;
  attemptId: string;
  ordinal: number;
  engine: 'notStarted' | 'running' | 'succeeded' | 'failed' | 'cancelled' | 'unknown';
  business: 'unverified' | 'succeeded' | 'partial' | 'failed' | 'notApplicable' | 'cancelled';
  lifecycle: 'notStarted' | 'running' | 'completed' | 'cancelled' | 'interrupted' | 'blocked' | 'skipped';
  recovery: 'notRequired' | 'pending' | 'restored' | 'quarantined';
  reasonCode: string;
}
export interface RecordRow {
  runId: string;
  automationId: string;
  userId: string | null;
  endedAt: string;
  durationMilliseconds: number | null;
  durationKind: 'measured' | 'checkpointLowerBound' | 'unknown';
  endTimeKind: 'measuredTerminal' | 'reconciledTerminalObservation' | 'declinedRetryObservation';
  finalAttempt: AttemptFacts;
}
export interface RecordPage { items: RecordRow[]; next?: { endedAt: string; runId: string } }
export interface HistoryAttempt { attemptId: string; ordinal: number; facts: AttemptFacts }
export interface AttemptTiming { attemptId: string; startedAt: string | null; endedAt: string | null; durationMilliseconds: number | null }
export interface HistoryDisplay {
  steps: { stepId: string; scriptName: string }[];
  accounts: { userId: string; name: string; stepIds: string[] }[];
  runId: string;
  targetName: string;
  userName: string | null;
  mode: 'manual' | 'auto' | null;
  maximumAttempts: number;
  startedAt: string | null;
  attempts: AttemptTiming[];
}
export interface HistoryImage {
  outcome: 'captured' | 'unavailable';
  invocationOrdinal: number;
  key: { stepId: string; captureId: string };
  reasonCode: string;
  resultSha256: string;
}
export function historyImage(value: unknown, runId: string, attemptId: string): HistoryImage {
  const row = objectValue(value), key = objectValue(row.key);
  if (key.runId !== runId || key.attemptId !== attemptId) throw new TypeError('history_image_unbound');
  const resultSha256 = stringValue(row.resultSha256);
  if (!/^[a-f0-9]{64}$/.test(resultSha256)) throw new TypeError('history_image_digest_invalid');
  return { outcome: enumValue(row.outcome, ['captured', 'unavailable']),
    invocationOrdinal: boundedInteger(row.invocationOrdinal, 1, 99),
    key: { stepId: stringValue(key.stepId), captureId: uuidValue(key.captureId) },
    reasonCode: stringValue(row.reasonCode), resultSha256 };
}
const nullableDate = (value: unknown) => value === null ? null : dateValue(value);
const duration = (value: unknown) => value === null ? null : boundedInteger(value, 0, 31536000000);
export function attemptFacts(value: unknown): AttemptFacts {
  const row = objectValue(value);
  return { runId: uuidValue(row.runId), attemptId: uuidValue(row.attemptId), ordinal: boundedInteger(row.ordinal, 1, 99),
    engine: enumValue(row.engine, ['notStarted', 'running', 'succeeded', 'failed', 'cancelled', 'unknown']),
    business: enumValue(row.business, ['unverified', 'succeeded', 'partial', 'failed', 'notApplicable', 'cancelled']),
    lifecycle: enumValue(row.lifecycle, ['notStarted', 'running', 'completed', 'cancelled', 'interrupted', 'blocked', 'skipped']),
    recovery: enumValue(row.recovery, ['notRequired', 'pending', 'restored', 'quarantined']), reasonCode: stringValue(row.reasonCode) };
}
export function recordRow(value: unknown, expectedRunId?: string): RecordRow {
  const row = objectValue(value), facts = attemptFacts(row.finalAttempt);
  if (expectedRunId && facts.runId !== expectedRunId || ['notStarted', 'running'].includes(facts.lifecycle) || facts.recovery === 'pending')
    throw new TypeError('record_identity_unbound');
  const result: RecordRow = { runId: facts.runId, automationId: uuidValue(row.automationId),
    userId: row.userId === null ? null : uuidValue(row.userId), endedAt: dateValue(row.endedAt),
    durationMilliseconds: duration(row.durationMilliseconds), durationKind: enumValue(row.durationKind, ['measured', 'checkpointLowerBound', 'unknown']),
    endTimeKind: enumValue(row.endTimeKind, ['measuredTerminal', 'reconciledTerminalObservation', 'declinedRetryObservation']), finalAttempt: facts };
  if ((result.durationKind === 'unknown') !== (result.durationMilliseconds === null)) throw new TypeError('record_duration_invalid');
  return result;
}
export function recordPage(value: unknown): RecordPage {
  const row = objectValue(value), items = arrayValue(row.items, item => recordRow(item));
  if (items.length > 200 || new Set(items.map(item => item.runId)).size !== items.length) throw new TypeError('record_page_invalid');
  for (let index = 1; index < items.length; index++) {
    const before = items[index - 1]!, item = items[index]!;
    if (Date.parse(item.endedAt) < Date.parse(before.endedAt) ||
        Date.parse(item.endedAt) === Date.parse(before.endedAt) && item.runId <= before.runId)
      throw new TypeError('record_page_order_invalid');
  }
  if (row.next == null) return { items };
  const cursor = objectValue(row.next), next = { endedAt: dateValue(cursor.endedAt), runId: uuidValue(cursor.runId) };
  const last = items.at(-1);
  if (!last || Date.parse(last.endedAt) !== Date.parse(next.endedAt) || last.runId !== next.runId)
    throw new TypeError('history_cursor_invalid');
  return { items, next };
}
export function historyAttempts(value: unknown, runId: string): HistoryAttempt[] {
  const result = objectValue(value);
  const items = arrayValue(result.items, entry => {
    const facts = attemptFacts(objectValue(entry).facts);
    if (facts.runId !== runId) throw new TypeError('history_attempt_identity_invalid');
    return { attemptId: facts.attemptId, ordinal: facts.ordinal, facts };
  });
  if (items.length > 99 || result.nextOrdinal != null || new Set(items.map(item => item.attemptId)).size !== items.length ||
      items.some((item, index) => index > 0 && item.ordinal <= items[index - 1]!.ordinal))
    throw new TypeError('history_attempt_cursor_invalid');
  return items;
}
export function historyDisplay(value: unknown, runId: string): HistoryDisplay {
  const row = objectValue(value);
  if (row.runId !== runId) throw new TypeError('history_display_unbound');
  const attempts = arrayValue(row.attempts, value => {
    const timing = objectValue(value);
    return { attemptId: uuidValue(timing.attemptId), startedAt: nullableDate(timing.startedAt),
      endedAt: nullableDate(timing.endedAt), durationMilliseconds: duration(timing.durationMilliseconds) };
  });
  if (attempts.length > 99 || new Set(attempts.map(item => item.attemptId)).size !== attempts.length)
    throw new TypeError('history_display_invalid');
  const accounts = arrayValue(row.accounts, value => {
    const account = objectValue(value);
    return { userId: uuidValue(account.userId), name: stringValue(account.name), stepIds: arrayValue(account.stepIds, stringValue) };
  });
  if (new Set(accounts.map(account => account.userId)).size !== accounts.length) throw new TypeError('record_accounts_invalid');
  return { runId, accounts, targetName: stringValue(row.targetName), userName: row.userName === null ? null : stringValue(row.userName),
    mode: row.mode === null ? null : enumValue(row.mode, ['manual', 'auto']),
    steps: arrayValue(row.steps, value => { const step = objectValue(value);
      return { stepId: stringValue(step.stepId), scriptName: stringValue(step.scriptName) }; }),
    maximumAttempts: boundedInteger(row.maximumAttempts, 1, 99), startedAt: nullableDate(row.startedAt), attempts };
}
