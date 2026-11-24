import type { AppStore } from './store';
import { extensionList } from '../control/extensions';
import { prepareExtensionIcons } from '../control/extension-icons';
import { sevenDays } from '../pages/dashboard-summary';
import { localDate, timeRange } from '../pages/history-format';
import { objectValue, arrayValue, type Decoder } from '../control/client';
import { retryRead } from '../control/read-retry';
import { PreparedReadRevoked } from '../control/control-reads';
import { dashboardCards } from '../control/interface';
import { notificationCatalog } from '../pages/notification-types';

export async function preparePages(store: AppStore, signal: AbortSignal,
  progress: (done: number, total: number) => void, foreground = false,
  settled = new Set<string>()): Promise<{ failures: unknown[]; revoked: number }> {
  const include = (...pages: string[]) => !foreground || pages.includes(store.state.page);
  const read = <T>(path: string, decode?: Decoder<T>) =>
    retryRead(() => store.client.prepareRead(path, signal, decode), signal);
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
      const summary = await read(path, value => {
        const row = objectValue(value);
        arrayValue(row.days, objectValue);
        return row;
      });
      const date = arrayValue(summary.days, objectValue)[0]?.date;
      if (innerWidth <= 820 || typeof date !== 'string') return;
      const day = timeRange(date, date), start = new Date(date + 'T00:00:00');
      await read('/control/v1/records/summary/users?' + new URLSearchParams({
        endedFromInclusive: day.from, endedUntilExclusive: day.until, offsetMinutes: String(-start.getTimezoneOffset()) }));
    } });
  }
  if (include('notifications') && store.can('notifications.read')) jobs.push({ key: '/control/v1/notifications', run: async () => {
    await retryRead(() => store.prepareEditorDirectory('notifications', notificationCatalog, signal), signal);
  } });
  if (include('plugins') && store.can('operations.read')) paths.push('/control/v1/extensions/operations?offset=0&limit=50');
  jobs.push(...paths.map(path => ({ key: path, run: async () => { await read(path); } })));
  if (include('plugins', 'scripts', 'users') && store.can('extensions.read')) jobs.push({ key: 'extension-icons', run: async () => {
    const list = await retryRead(() => store.prepareEditorDirectory('extensions', extensionList, signal), signal);
    await prepareExtensionIcons(store.client, list.items.filter(item => item.isInstalled).map(item => item.extensionId), signal);
  } });
  if (include('plugins') && store.can('extensions.read')) jobs.push({ key: 'extension-catalog', run: async () => {
    const list = await retryRead(() => store.prepareEditorDirectory('extensions', extensionList, signal), signal);
    if (list.hasReleaseTrust) await read('/control/v1/extensions/catalog/' +
      (store.state.settings?.saved.values.updates.pluginSource || 'stable'));
  } });
  const pending = jobs.filter(job => !settled.has(job.key));
  const failures: unknown[] = [];
  let next = 0, done = 0, revoked = 0;
  progress(0, pending.length);
  await Promise.all(Array.from({ length: Math.min(4, pending.length) }, async () => {
    while (next < pending.length && !signal.aborted) {
      const job = pending[next++]!;
      try { await job.run(); }
      catch (error) {
        if (!signal.aborted) {
          if (error instanceof PreparedReadRevoked) revoked++;
          else failures.push(error);
        }
      }
      finally { settled.add(job.key); progress(++done, pending.length); }
    }
  }));
  signal.throwIfAborted();
  return { failures, revoked };
}
