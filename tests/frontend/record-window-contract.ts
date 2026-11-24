import { HistoryPageCache } from '../../apps/web/src/pages/history-page-cache.js';
import type { RecordPage } from '../../apps/web/src/control/models.js';

export async function recordWindowContract(check: (condition: unknown, name: string) => void): Promise<void> {
  const window = new HistoryPageCache();
  const scope = new AbortController();
  const cached = new HistoryPageCache();
  let requests = 0;
  const cachedRead = async (cursor: RecordPage['next']): Promise<RecordPage> => {
    requests++;
    return { items: [{ runId: cursor ? 'second' : 'first' }],
      ...(cursor ? {} : { next: { endedAt: '2026-10-24T00:00:00Z', runId: 'first' } }) };
  };
  await cached.read(1, cachedRead, scope.signal, true, 'instance-and-permissions-one');
  await cached.read(2, cachedRead, scope.signal, false, 'instance-and-permissions-one');
  const clone = globalThis.structuredClone, stringify = JSON.stringify;
  let clones = 0, serializations = 0;
  globalThis.structuredClone = (value, options) => { clones++; return clone(value, options); };
  JSON.stringify = ((...args: Parameters<typeof JSON.stringify>) => { serializations++; return stringify(...args); }) as typeof JSON.stringify;
  const returned = await cached.read(1, cachedRead, scope.signal, false, 'instance-and-permissions-one').finally(() => {
    globalThis.structuredClone = clone;
    JSON.stringify = stringify;
  });
  check(clones === 1 && serializations === 0, `history cache hit copies once and skips serialization (${clones}/${serializations})`);
  check(returned.data.items[0]!.runId === 'first' && requests === 2, 'returning to a visited page reuses its verified rows');
  returned.data.items[0]!.runId = 'local-view-change';
  const isolated = await cached.read(1, cachedRead, scope.signal, false, 'instance-and-permissions-one');
  check(isolated.data.items[0]!.runId === 'first', 'display changes cannot modify cached record data');
  await cached.read(1, cachedRead, scope.signal, false, 'other-instance-or-permissions');
  check(requests === 3, 'another instance or permission scope reads its own current data');
  await cached.read(1, cachedRead, scope.signal, true, 'other-instance-or-permissions');
  check(requests === 4, 'an explicit refresh replaces cached pages');
  const id = (index: number) => '00000000-0000-4000-8000-' + index.toString(16).padStart(12, '0');
  const read = async (cursor: RecordPage['next']): Promise<RecordPage> => {
    const start = cursor ? parseInt(cursor.runId.slice(-12), 16) + 1 : 0;
    return { items: Array.from({length:50}, (_, offset) => ({runId:id(start + offset)})),
      next: { endedAt: '2026-10-24T00:00:00Z', runId: id(start + 49) } };
  };
  const capacity = new HistoryPageCache();
  let capacityReads = 0;
  const countReads = (cursor: RecordPage['next']) => { capacityReads++; return read(cursor); };
  await capacity.read(9, countReads, scope.signal, true);
  await capacity.read(2, countReads, scope.signal, false);
  await capacity.read(1, countReads, scope.signal, false);
  check(capacityReads === 10, 'history retains exactly eight recent pages including mutable-copy hits');
  await capacity.read(40, countReads, scope.signal, true);
  capacityReads = 0;
  await capacity.read(10, countReads, scope.signal, false);
  check(capacityReads === 10, 'history bounds its cursor window to 32 entries and keeps the first page available');

  let largeReads = 0;
  const readLarge = async (): Promise<RecordPage> => {
    largeReads++;
    return { items: [{ runId: 'x'.repeat(131072) }] };
  };
  await capacity.read(1, readLarge, scope.signal, true);
  await capacity.read(1, readLarge, scope.signal, false);
  check(largeReads === 2, 'pages over the serialized size limit are fetched again');
  let page = await window.read(67, read, scope.signal, true);
  check(page.number === 67 && page.data.items.length === 50 && page.data.items[0]!.runId === id(3300),
    'history seek follows stable cursors and returns only its requested row window');
  page = await window.read(3, read, scope.signal, false);
  check(page.number === 3 && page.data.items[0]!.runId === id(100),
    'history returns the requested earlier page after traversing a long history');
  let release!: (page: RecordPage) => void;
  const cancelled = new AbortController();
  const pending = window.read(80, () => new Promise(resolve => { release = resolve; }), cancelled.signal, false);
  cancelled.abort(); release({ items: [], next: { endedAt: '2026-10-24T00:00:00Z', runId: id(4000) } });
  let aborted = false;
  try { await pending; } catch (error) { aborted = error instanceof DOMException && error.name === 'AbortError'; }
  check(aborted, 'cancelled history reads do not accept a late page result');
  const refreshed = await window.read(68, read, scope.signal, false);
  check(refreshed.data.items[0]?.runId === id(3350), 'cancelled history reads never publish a cached page');
  let invalid = false;
  try {
    await new HistoryPageCache().read(3, async () => ({items:[], next:{endedAt:'2026-10-24T00:00:00Z',runId:id(0)}}), scope.signal, true);
  } catch (error) { invalid = error instanceof TypeError; }
  check(invalid, 'non-advancing history cursors fail instead of looping forever');
  page = await window.read(100, async cursor => ({ items: [{runId:cursor?.runId || id(0)}] }), scope.signal, true);
  check(page.number === 1 && !page.data.next, 'a shorter refreshed history stops at the actual cursor end without inventing pages');
  let groupRequests = 0;
  const groups = await new HistoryPageCache().read(1, async () => {
    groupRequests++;
    return { items: [] };
  }, scope.signal, true);
  check(groupRequests === 1 && groups.data.items.length === 0 && !groups.data.next,
    'an empty server-filtered group page needs no scan of unmatched account history');
}
