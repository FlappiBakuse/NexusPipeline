import assert from 'node:assert/strict';
import { accountSummaries } from '../../apps/web/src/control/account-summary';
import { ownerCases } from './account-owner';
export async function run(): Promise<string[]> {
  const response = { catalogRevision: 1, progressRevision: 0, observedAt: '2026-10-31T00:00:00Z',
    items: [{ userId: 'u', summaryReason: 'not_run', summaryTone: 'muted', runId: null, activeCount: 0, nextRunAt: null, queueId: null }] };
  assert.equal(accountSummaries(response, ['u']).items[0].summaryReason, 'not_run');
  assert.throws(() => accountSummaries(response, ['different']));
  assert.throws(() => accountSummaries({ ...response, items: [response.items[0], response.items[0]] }, ['u', 'other']));
  return ['summary accepts the current user set and rejects another identity', ...await ownerCases()];
}
