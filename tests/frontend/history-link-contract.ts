import { ControlClient } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';
import { HistoryLink } from '../../apps/web/src/pages/history-link';

export async function historyLinkContract(check: (value: unknown, name: string) => void): Promise<void> {
  const id = (value: number) => '10000000-0000-4000-8000-' + value.toString(16).padStart(12, '0');
  const original = location.hash;
  const reads: string[] = [];
  const client = new ControlClient(location.origin, async input => {
    const url = new URL(String(input)); reads.push(url.searchParams.get('userId') || '');
    const userId = url.searchParams.get('userId');
    return Response.json({ runId: id(1), automationId: id(2), userId, endedAt: '2026-11-06T00:00:00Z',
      durationMilliseconds: 1000, durationKind: 'measured', endTimeKind: 'measuredTerminal',
      finalAttempt: { runId: id(1), attemptId: id(3), ordinal: 1, engine: 'succeeded',
        business: userId === id(4) ? 'failed' : 'succeeded', lifecycle: 'completed', recovery: 'restored', reasonCode: 'fixture' } });
  });
  const store = createStore(client); store.state.page = 'history';
  let link = new HistoryLink(store, () => true, async () => {}, () => {});
  try {
    history.replaceState(null, '', '#/history?run=' + id(1) + '&user=' + id(4));
    await link.restore();
    check(link.selected.value?.userId === id(4) && link.selected.value.finalAttempt?.business === 'failed',
      'the account deep link loads the failed account result within the shared run');
    link.dispose(); link = new HistoryLink(store, () => true, async () => {}, () => {});
    await link.restore();
    check(reads.length === 2 && reads.every(user => user === id(4)) && location.hash.includes('&user=' + id(4)),
      'reopening the copied route preserves its account query and result');
    history.replaceState(null, '', '#/history?run=' + id(1) + '&user=invalid');
    await link.restore();
    check(!link.selected.value && !!link.deepLinkError.value && reads.length === 2,
      'an invalid account link gives an unavailable result without loading the entire run');
  } finally { link.dispose(); history.replaceState(null, '', original); }
}
