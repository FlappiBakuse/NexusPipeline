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

export class RecordWindow {
  private starts = new Map<number, Cursor | undefined>([[1, undefined]]);
  private pages = new Map<number, RecordPage>();
  private scope = '';

  async read(
    target: number,
    fetchPage: ReadPage,
    signal: AbortSignal,
    reset: boolean,
    scope = ''
  ): Promise<{ number: number; data: RecordPage }> {
    if (!Number.isSafeInteger(target) || target < 1) throw new RangeError('history_page_invalid');
    reset ||= scope !== this.scope;
    const starts = reset ? new Map<number, Cursor | undefined>([[1, undefined]]) : new Map(this.starts);
    const pages = reset ? new Map<number, RecordPage>() : new Map(this.pages);
    let number = Math.max(...[...starts.keys()].filter((page) => page <= target));
    for (;;) {
      signal.throwIfAborted();
      const cursor = starts.get(number);
      const cached = pages.get(number);
      const data = cached ? structuredClone(cached) : await fetchPage(cursor);
      signal.throwIfAborted();
      const next = data.next;
      validateCursor(next, cursor);
      const known = starts.get(number + 1);
      if (!next || (known && (known.endedAt !== next.endedAt || known.runId !== next.runId))) {
        for (const page of starts.keys()) if (page > number) starts.delete(page);
        for (const page of pages.keys()) if (page > number) pages.delete(page);
      }
      if (next) {
        starts.delete(number + 1);
        starts.set(number + 1, { ...next });
      }
      pages.delete(number);
      if (JSON.stringify(data).length <= 131072) pages.set(number, structuredClone(data));
      while (pages.size > 8) pages.delete(pages.keys().next().value!);
      while (starts.size > 32) starts.delete([...starts.keys()].find((page) => page !== 1)!);
      if (number >= target || !next) {
        this.starts = starts;
        this.pages = pages;
        this.scope = scope;
        return { number, data };
      }
      number++;
    }
  }
}
