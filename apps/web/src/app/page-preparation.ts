import type { AppStore } from './store';
import { extensionList } from '../control/extensions';
import { prepareExtensionIcons } from '../control/extension-icons';
import { sevenDays } from '../pages/dashboard-summary';
import { localDate, timeRange } from '../pages/history-range';
import { objectValue, arrayValue } from '../control/client';
import { retryRead } from '../control/read-retry';
import { dashboardCards } from '../control/interface';

export async function preparePages(store: AppStore, signal: AbortSignal,
  progress: (done: number, total: number) => void, report: (error: unknown) => void, foreground = false,
  completed = new Set<string>()): Promise<void> {
  const include = (...pages: string[]) => !foreground || pages.includes(store.state.page);
  const paths: string[] = [];
  const jobs: { key: string; run: () => Promise<void> }[] = [];
  if (include('dispatch', 'dashboard') && store.can('execution.read')) paths.push('/control/v1/execution/active');
  const layout = store.state.interface;
  if (store.can('records.read') && include('dashboard') && layout &&
      (layout.revision === 0 || dashboardCards(layout.values.dashboardCards).includes('history-duration')))
    paths.push('/control/v1/records/summary/days?' + sevenDays().query);
  if (include('history') && store.can('records.read')) {
    const today = new Date();
    const range = timeRange(localDate(new Date(today.getFullYear(), today.getMonth(), today.getDate() - 29)), localDate(today));
    const path = '/control/v1/records/summary/days?' + new URLSearchParams({ endedFromInclusive: range.from,
      endedUntilExclusive: range.until, offsetMinutes: String(-today.getTimezoneOffset()) });
    jobs.push({ key: path, run: async () => {
      const summary = await store.client.prepareRead(path, signal, value => {
        const row = objectValue(value);
        arrayValue(row.days, objectValue);
        return row;
      });
      const date = arrayValue(summary.days, objectValue)[0]?.date;
      if (innerWidth <= 820 || typeof date !== 'string') return;
      const day = timeRange(date, date), start = new Date(date + 'T00:00:00');
      await store.client.prepareRead('/control/v1/records/summary/users?' + new URLSearchParams({
        endedFromInclusive: day.from, endedUntilExclusive: day.until, offsetMinutes: String(-start.getTimezoneOffset()) }), signal);
    } });
  }
  if (include('notifications') && store.can('notifications.read')) paths.push('/control/v1/notifications');
  if (include('plugins') && store.can('operations.read')) paths.push('/control/v1/extensions/operations?offset=0&limit=50');
  jobs.push(...paths.map(path => ({ key: path, run: async () => { await store.client.prepareRead(path, signal); } })));
  if (include('plugins', 'scripts', 'users') && store.can('extensions.read')) jobs.push({ key: 'extension-icons', run: async () => {
    const list = await store.client.prepareRead('/control/v1/extensions', signal, extensionList);
    await prepareExtensionIcons(store.client, list.items.filter(item => item.isInstalled).map(item => item.extensionId), signal);
  } });
  if (include('plugins') && store.can('extensions.read')) jobs.push({ key: 'extension-catalog', run: async () => {
    const list = await store.client.prepareRead('/control/v1/extensions', signal, extensionList);
    if (list.hasReleaseTrust) await store.client.prepareRead('/control/v1/extensions/catalog/' +
      (store.state.settings?.saved.values.updates.pluginSource || 'stable'), signal);
  } });
  const pending = jobs.filter(job => !completed.has(job.key));
  let next = 0, done = 0;
  progress(0, pending.length);
  await Promise.all(Array.from({ length: Math.min(4, pending.length) }, async () => {
    while (next < pending.length && !signal.aborted) {
      const job = pending[next++]!;
      try { await retryRead(job.run, signal); completed.add(job.key); } catch (error) { if (!signal.aborted) report(error); }
      finally { progress(++done, pending.length); }
    }
  }));
  signal.throwIfAborted();
}
