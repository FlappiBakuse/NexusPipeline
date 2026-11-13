import assert from 'node:assert/strict';
import { executionPreview, executionStep } from '../../apps/web/src/control/execution';
import { attemptFacts, historyImage, recordRow, recordPage } from '../../apps/web/src/control/records';
import { extensionMetadata, titleLocales } from '../../apps/web/src/control/extension-metadata';
import { adapterResult } from '../../apps/web/src/pages/adapter-results';
export async function run(): Promise<string[]> {
  const selection = { bindingId: 'binding', expectedBindingRevision: 2, extensionId: 'bettergi',
    expectedInstalledRevision: 1, resources: [] };
  const discovery = adapterResult({ source: { bindingId: 'binding', bindingRevision: 2, extensionId: 'bettergi',
    installedRevision: 1, resources: [] }, diagnostics: [],
    coverage: { selected: 1, projected: 1, retrySafe: 1, isComplete: true, hasBlockingDiagnostics: false },
    gamePathCheck: 'notApplicable', tasks: [{ taskId: 'task', title: '任务', titleLocales: {}, isSelected: true, isEnabled: true }] },
    'discover', selection, undefined, '');
  assert.equal(discovery.tasks[0]?.title, '任务');
  const cases = ['adapter discovery accepts an empty optional locale map and preserves the task title'];
  const preview = { hasRunnable: true, canPrepareInitialConfiguration: false, hasBlockedItems: false, reasonCode: null, completionAction: 'hibernate',
    groups: [{ scriptId: 'script' }], items: [{ scriptId: 'script', userId: 'user', disposition: 'runnable', reasonCode: 'execution_inputs_verified' }] };
  const parsedPreview = executionPreview(preview);
  assert.equal(parsedPreview.completionAction, 'hibernate');
  assert.equal(parsedPreview.hasBlockedItems, false);
  for (const [field, value] of [['hasRunnable', 'true'], ['hasBlockedItems', 0], ['completionAction', 'sleep'], ['groups', {}], ['items', null]]) {
    assert.throws(() => executionPreview({ ...preview, [field as string]: value })); cases.push('full execution preview rejects malformed ' + field);
  }
  const facts = { runId: crypto.randomUUID(), attemptId: crypto.randomUUID(), ordinal: 1, engine: 'failed', business: 'succeeded',
    lifecycle: 'completed', recovery: 'restored', reasonCode: 'execution_business_succeeded' };
  assert.equal(attemptFacts(facts).business, 'succeeded');
  for (const [field, value] of [['runId', 'foreign'], ['attemptId', null], ['ordinal', 0], ['engine', 'success'], ['lifecycle', 'done']]) {
    assert.throws(() => attemptFacts({ ...facts, [field as string]: value })); cases.push('attempt facts reject malformed ' + field);
  }
  assert.throws(() => attemptFacts({ ...facts, ordinal: 100 }));
  const row = { automationId: crypto.randomUUID(), userId: null, endedAt: '2026-11-01T00:00:00Z', durationMilliseconds: null,
    durationKind: 'unknown', endTimeKind: 'measuredTerminal', finalAttempt: facts };
  assert.equal(recordRow(row).durationMilliseconds, null);
  assert.throws(() => recordRow({ ...row, durationMilliseconds: 5 }));
  assert.throws(() => recordRow({ ...row, finalAttempt: { ...facts, recovery: 'pending' } }));
  assert.throws(() => recordRow(row, crypto.randomUUID()));
  assert.throws(() => recordPage({ items: [row, { ...row, endedAt: '2026-11-02T00:00:00Z' }] }));
  cases.push('record identity timing and duplicate rows preserve the current boundary');
  assert.equal(extensionMetadata(undefined), undefined); assert.throws(() => titleLocales({ invalid_locale: 'text' }));
  cases.push('locale declarations reject unknown locale shape');
  const invocation = { ordinal: 1, engine: 'Succeeded', business: 'Succeeded', reasonCode: 'execution_completed',
    startedAt: '2026-11-03T00:00:00Z', endedAt: '2026-11-03T00:00:01Z', durationMilliseconds: 1000 };
  const step = { stepId: 'step', scriptId: 'script', userId: null, bindingId: null, dependsOn: [], phase: 'collected',
    tasks: [], logRoles: ['main'], invocations: [invocation] };
  assert.deepEqual(executionStep(step).invocations?.[0], invocation);
  assert.equal(executionStep({ ...step, invocationOrdinal: 2, maximumInvocations: 3 }).invocationOrdinal, 2);
  for (const invocationOrdinal of [-1, 100, '2']) assert.throws(() => executionStep({ ...step, invocationOrdinal }));
  for (const [field, value] of [['startedAt', 'invalid'], ['endedAt', false], ['durationMilliseconds', -1]])
    assert.throws(() => executionStep({ ...step, invocations: [{ ...invocation, [field as string]: value }] }));
  assert.equal(executionStep({ ...step, invocations: [{ ...invocation, startedAt: null, durationMilliseconds: null }] })
    .invocations?.[0]?.durationMilliseconds, null);
  cases.push('invocation timings preserve measured values and reject malformed dates or durations');
  const image = { outcome: 'unavailable', invocationOrdinal: 1,
    key: { runId: facts.runId, attemptId: facts.attemptId, stepId: 'step', captureId: crypto.randomUUID() },
    reasonCode: 'execution_capture_deadline', resultSha256: 'a'.repeat(64) };
  assert.equal(historyImage(image, facts.runId, facts.attemptId).outcome, 'unavailable');
  assert.equal(historyImage({ ...image, outcome: 'captured' }, facts.runId, facts.attemptId).outcome, 'captured');
  assert.throws(() => historyImage({ ...image, outcome: 'pending' }, facts.runId, facts.attemptId));
  cases.push('history capture outcomes keep unavailable receipts distinct from actual images');
  return cases;
}
