export interface UserPage {
  items: Record<string, unknown>[];
  next: string | null;
}

export class UserWindow {
  private starts = new Map<number, string | undefined>([[1, undefined]]);

  async read(
    target: number,
    fetchPage: (cursor?: string) => Promise<UserPage>,
    signal: AbortSignal
  ): Promise<{ number: number; data: UserPage }> {
    if (!Number.isSafeInteger(target) || target < 1) throw new RangeError('history_page_invalid');
    const starts = new Map(this.starts);
    let number = Math.max(...[...starts.keys()].filter((page) => page <= target));
    for (;;) {
      signal.throwIfAborted();
      const cursor = starts.get(number),
        data = await fetchPage(cursor);
      signal.throwIfAborted();
      if (data.items.length > 200 || (data.next !== null && (!data.next || data.next === cursor)))
        throw new TypeError('history_user_cursor_invalid');
      if (starts.get(number + 1) !== data.next) {
        for (const page of starts.keys()) if (page > number) starts.delete(page);
      }
      if (data.next) starts.set(number + 1, data.next);
      while (starts.size > 32) starts.delete([...starts.keys()].find((page) => page !== 1)!);
      if (number >= target || !data.next) {
        this.starts = starts;
        return { number, data };
      }
      number++;
    }
  }
}
