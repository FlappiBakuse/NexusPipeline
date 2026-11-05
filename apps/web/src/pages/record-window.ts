import type { RecordPage } from '../control/models';

type Cursor = NonNullable<RecordPage['next']>;
type ReadPage = (cursor: Cursor | undefined) => Promise<RecordPage>;

function validateCursor(next: Cursor | undefined, previous: Cursor | undefined): void {
  if (
    next &&
    (typeof next.endedAt !== 'string' ||
      typeof next.runId !== 'string' ||
      !next.endedAt ||
      !next.runId ||
      (previous && next.endedAt === previous.endedAt && next.runId === previous.runId))
  )
    throw new TypeError('history_cursor_invalid');
}

export async function groupPage(
  cursor: Cursor | undefined,
  read: (cursor: Cursor | undefined, limit: number) => Promise<RecordPage>,
  signal: AbortSignal
): Promise<RecordPage> {
  const items: RecordPage['items'] = [];
  let boundary: Cursor | undefined;
  for (;;) {
    signal.throwIfAborted();
    const data = await read(cursor, items.length < 50 ? 50 - items.length : 50);
    signal.throwIfAborted();
    validateCursor(data.next, cursor);
    const matching = data.items.filter((item) => item.userId == null);
    if (items.length === 50 && matching.length) return { ...data, items, next: boundary! };
    if (items.length < 50) {
      items.push(...matching);
      if (items.length === 50) boundary = data.next;
    }
    if (!data.next) {
      const result = { ...data, items };
      delete result.next;
      return result;
    }
    // Look ahead without retaining rows so Next means another matching page exists.
    cursor = data.next;
  }
}

export class RecordWindow {
  private starts = new Map<number, Cursor | undefined>([[1, undefined]]);

  async read(
    target: number,
    fetchPage: ReadPage,
    signal: AbortSignal,
    reset: boolean
  ): Promise<{ number: number; data: RecordPage }> {
    if (!Number.isSafeInteger(target) || target < 1) throw new RangeError('history_page_invalid');
    const starts = reset ? new Map<number, Cursor | undefined>([[1, undefined]]) : new Map(this.starts);
    let number = Math.max(...[...starts.keys()].filter((page) => page <= target));
    for (;;) {
      signal.throwIfAborted();
      const cursor = starts.get(number);
      const data = await fetchPage(cursor);
      signal.throwIfAborted();
      const next = data.next;
      validateCursor(next, cursor);
      const known = starts.get(number + 1);
      if (!next || (known && (known.endedAt !== next.endedAt || known.runId !== next.runId))) {
        for (const page of starts.keys()) if (page > number) starts.delete(page);
      }
      if (next) {
        starts.delete(number + 1);
        starts.set(number + 1, { ...next });
      }
      // Retain cursor bookmarks, never prior row arrays or mounted extension surfaces.
      while (starts.size > 32) starts.delete([...starts.keys()].find((page) => page !== 1)!);
      if (number >= target || !next) {
        this.starts = starts;
        return { number, data };
      }
      number++;
    }
  }
}
