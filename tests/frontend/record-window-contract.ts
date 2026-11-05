import { RecordWindow, groupPage } from '../../apps/web/src/pages/record-window.js';
import type { RecordPage } from '../../apps/web/src/control/models.js';

export async function recordWindowContract(check: (condition: unknown, name: string) => void): Promise<void> {
  const window = new RecordWindow();
  const scope = new AbortController();
  const id = (index: number) => '00000000-0000-4000-8000-' + index.toString(16).padStart(12, '0');
  let requests = 0;
  const read = async (cursor: RecordPage['next']): Promise<RecordPage> => {
    requests++;
    const start = cursor ? parseInt(cursor.runId.slice(-12), 16) + 1 : 0;
    return { items: Array.from({length:50}, (_, offset) => ({runId:id(start + offset)})),
      next: { endedAt: '2026-10-24T00:00:00Z', runId: id(start + 49) } };
  };
  let page = await window.read(67, read, scope.signal, true);
  check(page.number === 67 && page.data.items.length === 50 && page.data.items[0]!.runId === id(3300),
    'history seek follows stable cursors and returns only its requested row window');
  const previous = requests;
  page = await window.read(3, read, scope.signal, false);
  check(page.number === 3 && page.data.items[0]!.runId === id(100) && requests - previous === 3,
    'history can revisit an evicted cursor bookmark by seeking again from the retained root');
  let release!: (page: RecordPage) => void;
  const cancelled = new AbortController();
  const pending = window.read(80, () => new Promise(resolve => { release = resolve; }), cancelled.signal, false);
  cancelled.abort(); release({ items: [], next: { endedAt: '2026-10-24T00:00:00Z', runId: id(4000) } });
  let aborted = false;
  try { await pending; } catch (error) { aborted = error instanceof DOMException && error.name === 'AbortError'; }
  check(aborted, 'cancelled history reads do not accept a late page result');
  let invalid = false;
  try {
    await new RecordWindow().read(3, async () => ({items:[], next:{endedAt:'2026-10-24T00:00:00Z',runId:id(0)}}), scope.signal, true);
  } catch (error) { invalid = error instanceof TypeError; }
  check(invalid, 'non-advancing history cursors fail instead of looping forever');
  page = await window.read(100, async cursor => ({ items: [{runId:cursor?.runId || id(0)}] }), scope.signal, true);
  check(page.number === 1 && !page.data.next, 'a shorter refreshed history stops at the actual cursor end without inventing pages');
  const mixed = (total: number) => async (cursor: RecordPage['next'], limit: number): Promise<RecordPage> => {
    const start = cursor ? parseInt(cursor.runId.slice(-12), 16) + 1 : 0;
    const items = Array.from({length:Math.min(limit, total - start)}, (_, offset) => ({runId:id(start + offset),
      ...(start + offset & 1 ? {userId:id(999)} : {})}));
    const last = start + items.length - 1;
    return { items, ...(last < total - 1 ? {next:{endedAt:'2026-10-24T00:00:00Z',runId:id(last)}} : {}) };
  };
  let groups = await groupPage(undefined, mixed(101), scope.signal);
  check(groups.items.length === 50 && groups.next?.runId === id(98),
    'script-group windows fill matching rows and retain the exact boundary before lookahead');
  groups = await groupPage(groups.next, mixed(101), scope.signal);
  check(groups.items.length === 1 && groups.items[0]!.runId === id(100) && !groups.next,
    'the next script-group window retains the lookahead match and reaches the actual end');
  groups = await groupPage(undefined, mixed(100), scope.signal);
  check(groups.items.length === 50 && !groups.next,
    'trailing account records do not advertise an empty script-group page');
}
