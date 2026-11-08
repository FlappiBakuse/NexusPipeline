import { ExecutionCommands, type ExecutionCommandContext } from '../../apps/web/src/pages/execution-commands.js';
import { ControlClient } from '../../apps/web/src/control/client.js';
import type { ActiveRun } from '../../apps/web/src/control/execution.js';
import { executionPreview } from '../../apps/web/src/control/execution.js';
import { planAccounts } from '../../apps/web/src/pages/execution-plan.js';

function requireValue(value: unknown, message: string, checks: string[]): void {
  if (!value) throw new Error(message);
  checks.push(message);
}

export async function executionCommandContract(): Promise<{ checks: string[]; checkCount: number }> {
  const checks: string[] = [];
  const item = { scriptId: 'script-a', userId: 'user-a', disposition: 'runnable',
    reasonCode: 'execution_inputs_verified', successfulRunsToday: 0, maximumSuccessfulRunsPerDay: -1 };
  const preview = (value: unknown) => executionPreview({ hasRunnable: true, canPrepareInitialConfiguration: false, hasBlockedItems: false, reasonCode: null,
    completionAction: 'none', groups: [], items: [value] });
  requireValue(planAccounts(preview(item))[0]?.maximumSuccessfulRunsPerDay === -1,
    'an unlimited daily quota remains a runnable account in the plan', checks);
  requireValue(planAccounts(preview({ ...item, maximumSuccessfulRunsPerDay: 7 }))[0]?.maximumSuccessfulRunsPerDay === 7,
    'a finite daily quota retains its actual limit', checks);
  for (const invalid of [{ ...item, maximumSuccessfulRunsPerDay: -2 }, { ...item, successfulRunsToday: -1 }]) {
    let rejected = false;
    try { preview(invalid); } catch (error) { rejected = error instanceof TypeError; }
    requireValue(rejected, 'invalid negative plan counts remain rejected: ' + JSON.stringify(invalid), checks);
  }
  const requests: { path: string; body?: Record<string, unknown> }[] = [];
  let answer: Record<string, unknown> = {};
  let loseResponse = true;
  const client = new ControlClient('http://execution.test', async (input, init) => {
    const path = new URL(String(input)).pathname;
    const body = init?.body ? JSON.parse(String(init.body)) : undefined;
    requests.push({ path, body });
    if (body && loseResponse) throw new TypeError('transport_lost');
    return new Response(JSON.stringify(answer), { headers: { 'Content-Type': 'application/json' } });
  });
  const errors: unknown[] = [];
  const store = { client, drafts: new Map(), dirty: new Set(), can: () => true,
    state: { capabilities: { instanceId: 'instance-a' }, uncertainOperation: '' },
    report: (error: unknown) => errors.push(error) } as unknown as ExecutionCommandContext;
  const first = new ExecutionCommands(store);
  const selection = { format: 'nexus.control.execution-selection', version: 1,
    expectedCatalogRevision: 7, expectedSettingsRevision: 3, target: { kind: 'script', scriptId: 'script-a' } };
  requireValue(!await first.start(selection), 'a lost start response remains unresolved', checks);
  const original = first.state.pending!;
  selection.target.scriptId = 'script-b';
  requireValue((original.body.target as Record<string, unknown>).scriptId === 'script-a',
    'the pending command retains its original selection after visible selection changes', checks);
  const remounted = new ExecutionCommands(store);
  await remounted.start(selection);
  requireValue(remounted.state.pending?.id === original.id && requests.filter(item => item.body).length === 1,
    'remounting retains the same pending operation and cannot send another start', checks);
  answer = { operationId: crypto.randomUUID(), kind: 'execution.start', status: 'completed' };
  requireValue(!await remounted.inspect() && !!remounted.state.pending,
    'a successful receipt for a different operation cannot unlock execution', checks);
  answer = { operationId: original.id, kind: 'automation.change', status: 'completed' };
  requireValue(!await remounted.inspect() && !!remounted.state.pending,
    'a receipt for another operation kind cannot unlock execution', checks);
  answer = { operationId: original.id, kind: 'execution.start', status: 'pending' };
  requireValue(!await remounted.inspect() && !!remounted.state.pending,
    'an unfinished original operation remains locked', checks);
  answer = { ...answer, status: 'accepted' };
  requireValue(await remounted.inspect() && !remounted.state.pending && !store.dirty.size,
    'the exact accepted original operation resolves without replaying its write', checks);
  const run = { operationId: crypto.randomUUID(), runId: crypto.randomUUID(), attemptId: crypto.randomUUID() } as ActiveRun;
  requireValue(!await remounted.cancel(run), 'a lost cancellation response retains its command', checks);
  const cancellation = remounted.state.pending!;
  run.attemptId = crypto.randomUUID();
  requireValue(cancellation.target?.attemptId !== run.attemptId &&
    requests.at(-1)?.path.includes(cancellation.target!.operationId),
    'cancellation binds the original start, run and attempt before transport', checks);
  answer = { operationId: cancellation.id, kind: 'execution.start', status: 'completed' };
  requireValue(!await remounted.inspect(), 'a start receipt cannot prove cancellation', checks);
  answer = { ...answer, kind: 'execution.retry.cancel' };
  requireValue(await remounted.inspect(), 'an exact retry-declined cancellation resolves its original command', checks);
  loseResponse = false;
  answer = { acceptance: { operationId: crypto.randomUUID(), status: 'accepted' },
    runId: crypto.randomUUID(), attemptId: crypto.randomUUID(), acknowledgement: 'accepted' };
  requireValue(!await remounted.start(selection) && !!remounted.state.pending,
    'a malformed or foreign direct acknowledgement is an unknown write outcome', checks);
  const reads = requests.length;
  store.state.capabilities!.instanceId = 'instance-b';
  requireValue(!await remounted.inspect() && requests.length === reads,
    'an owner from a replaced Host instance cannot query or release the old command', checks);
  requireValue(requests.filter(item => item.body).length === 3 && errors.length >= 6,
    'only the three explicit commands were transported through failure and reconciliation cases', checks);
  return { checks, checkCount: checks.length };
}
