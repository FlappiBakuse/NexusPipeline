import { CompletionCommands, type CompletionContext } from '../../apps/web/src/pages/completion-commands.js';
import { ControlClient } from '../../apps/web/src/control/client.js';

export async function completionCommands(): Promise<{ checks: string[]; checkCount: number }> {
  const checks: string[] = [];
  const requireValue = (value: unknown, description: string): void => {
    if (!value) throw new Error(description);
    checks.push(description);
  };
  let response: unknown = {};
  let failed = false;
  const writes: string[] = [];
  const client = new ControlClient('http://completion.test', async input => {
    writes.push(new URL(String(input)).pathname);
    if (failed) throw new TypeError('connection_lost');
    return new Response(JSON.stringify(response), { headers: { 'content-type': 'application/json' } });
  });
  const store = { client, drafts: new Map(), state: { capabilities: { instanceId: 'a' } },
    can: () => true, report: () => {} } satisfies CompletionContext;
  const first = new CompletionCommands(store);
  requireValue(!await first.cancel('completion-a') && first.state.pending === 'completion-a',
    'a malformed successful response retains the original completion identity');
  const second = new CompletionCommands(store);
  requireValue(!await second.cancel('completion-a') && writes.length === 1,
    'a remounted notice cannot repeat an unresolved cancellation');
  second.observe('completion-a', 'Entered');
  requireValue(second.state.pending === 'completion-a', 'an entered system action is not inferred to be cancelled');
  second.observe(null, null);
  requireValue(!second.state.pending, 'a verified pending-state read releases an absent original countdown');
  failed = true;
  requireValue(!await second.cancel('completion-b') && second.state.pending === 'completion-b',
    'a lost response retains the second original cancellation');
  store.state.capabilities.instanceId = 'b';
  second.observe(null, null);
  requireValue(!await second.cancel('completion-c') && second.state.pending === 'completion-b' && writes.length === 2,
    'a replaced Host instance cannot query away or repeat the old cancellation');
  const third = new CompletionCommands(store);
  failed = false;
  response = { cancelled: true };
  requireValue(await third.cancel('completion-c') && third.state.pending === 'completion-c',
    'the new instance can cancel its own countdown and await a state read');
  third.observe('completion-c', 'Cancelled');
  requireValue(!third.state.pending && writes.at(-1)?.endsWith('/completion-c/cancel'),
    'an explicit cancelled observation releases exactly the current countdown');
  return { checks, checkCount: checks.length };
}
