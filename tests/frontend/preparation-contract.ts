import { ControlClient, objectValue } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';
import { interfacePreparation } from '../../apps/web/src/app/interface-preparation';
import { preparePages } from '../../apps/web/src/app/page-preparation';
import { HostSnapshots } from '../../apps/web/src/app/host-snapshots';
import { FrontendRegistry } from '../../apps/web/src/extensions/frontend-registry';
import type { FrontendLoader } from '../../apps/web/src/extensions/frontend-loader';
import { prepareExtensionIcons, extensionIcon, clearExtensionIcons } from '../../apps/web/src/control/extension-icons';

export async function preparationContract(check: (value: unknown, name: string) => void): Promise<void> {
  let requests = 0;
  const client = new ControlClient(location.origin, async () => { requests++; return Response.json({ value: requests }); });
  const signal = new AbortController().signal;
  await client.prepareRead('/control/v1/notifications', signal);
  const snapshot = await client.read('/control/v1/notifications', objectValue);
  check(snapshot.value === 1 && requests === 1, 'first page entry consumes its prepared readonly snapshot');
  await client.read('/control/v1/notifications', objectValue);
  check(requests === 2, 'later page refresh reads current state');
  await client.prepareRead('/control/v1/extensions', signal);
  client.authenticate('');
  await client.read('/control/v1/extensions', objectValue);
  check(requests === 4, 'session changes revoke prepared snapshots');
  const store = createStore(client);
  store.start = async () => {};
  store.can = capability => capability === 'execution.read';
  const registry = new FrontendRegistry({ locale: 'en', appearance: 'dark', translate: (_id, key) => key });
  let release!: () => void;
  const slow = new Promise<void>(resolve => { release = resolve; });
  const loader = { synchronize: () => slow, cancelPending() {}, progress: () => {} } as unknown as FrontendLoader;
  const preparing = interfacePreparation(store, loader, registry);
  const pending = preparing.prepare();
  await Promise.resolve();
  check(preparing.preparing.value && !preparing.surfacesReady.value, 'startup retains progress while plugins prepare');
  await new Promise(resolve => setTimeout(resolve, 0));
  check(requests > 4, 'current page readonly data prepares while a plugin is still pending');
  await client.read('/control/v1/execution/active', objectValue);
  release(); await pending;
  check(requests === 5, 'background preparation does not reload a consumed current page snapshot');
  check(!preparing.preparing.value && preparing.surfacesReady.value, 'prepared page and surfaces complete before presentation');
  store.start = async () => { throw new Error('authentication_failed'); };
  let denied = false;
  try { await preparing.prepare(); } catch { denied = true; }
  check(denied && preparing.surfacesReady.value && !preparing.preparing.value,
    'failed background revalidation preserves the displayed interface');
  store.start = async () => {};
  const original = globalThis.setTimeout;
  let expire: (() => void) | undefined, finish!: () => void;
  globalThis.setTimeout = ((callback: TimerHandler, delay?: number, ...args: unknown[]) => {
    if (delay === 5000) { expire = callback as () => void; return 0; }
    return original(callback, delay, ...args);
  }) as typeof setTimeout;
  try {
    loader.synchronize = () => new Promise<void>(resolve => { finish = resolve; });
    const timed = preparing.prepare();
    await Promise.resolve(); await Promise.resolve();
    expire!();
    await Promise.resolve(); await Promise.resolve(); await Promise.resolve();
    check(!preparing.failed.value && preparing.surfacesReady.value && !preparing.preparing.value,
      'slow background preparation keeps the existing interface visible without timeout errors');
    finish(); await timed;
    loader.synchronize = async () => {};
    const retry = preparing.prepare();
    check(!preparing.preparing.value, 'retry never returns an already displayed interface to the startup screen');
    await retry;
  } finally { globalThis.setTimeout = original; }
  preparing.cancel(); store.dispose();
  await iconPreparation(check);
  await revokedRead(check);
  await sharedReads(check);
  await summaryPreparation(check);
  await routePreparation(check);
  await preparationCancellation(check);
  await eventRevision(check);
}

async function iconPreparation(check: (value: unknown, name: string) => void): Promise<void> {
  let release!: () => void, finished = false;
  const wait = new Promise<void>(resolve => { release = resolve; });
  const requests: string[] = [];
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = 1;
  const png = await new Promise<Blob>(resolve => canvas.toBlob(blob => resolve(blob!), 'image/png'));
  const client = new ControlClient(location.origin, async input => {
    const id = new URL(String(input)).pathname.split('/')[4]!; requests.push(id);
    if (id === 'broken-icon') return Response.json({ code: 'unavailable' }, { status: 503 });
    if (id === 'slow-icon') await wait;

    return new Response(png, { headers: { 'Content-Type': 'image/png' } });
  });
  try {
    const work = prepareExtensionIcons(client, ['broken-icon', 'slow-icon', 'third-icon', 'fourth-icon', 'last-icon'], new AbortController().signal)
      .then(() => { throw new Error('Expected icon fault'); }, () => { finished = true; });
    await new Promise(resolve => setTimeout(resolve, 0));
    check(!finished, 'one icon failure does not complete readiness while another icon is pending');
    release(); await work;
    check(requests.includes('last-icon') && extensionIcon('slow-icon').startsWith('blob:'),
      'icon preparation retains successful icons and attempts remaining declarations after a failure');
  } finally { release(); clearExtensionIcons(); }
}

async function revokedRead(check: (value: unknown, name: string) => void): Promise<void> {
  let respond!: (value: Response) => void;
  const client = new ControlClient(location.origin, () => new Promise(resolve => { respond = resolve; }));
  const prepared = client.prepareRead('/control/v1/notifications', new AbortController().signal);
  const owner = new AbortController();
  const waiting = client.read('/control/v1/notifications', objectValue, owner.signal);
  owner.abort();
  let stopped = false;
  try { await waiting; } catch { stopped = true; }
  check(stopped, 'a page cancels its prepared snapshot wait without waiting for the underlying request');
  client.clearPrepared(); respond(Response.json({ value: 'obsolete' }));
  let revoked = false;
  try { await prepared; } catch { revoked = true; }
  check(revoked, 'revoked prepared data cannot publish after a session or write generation change');
}

async function sharedReads(check: (value: unknown, name: string) => void): Promise<void> {
  const responses: ((value: Response) => void)[] = [];
  const client = new ControlClient(location.origin, () => new Promise(resolve => { responses.push(resolve); }));
  const prepOwner = new AbortController(), pageOwner = new AbortController();
  const first = client.prepareRead('/control/v1/notifications', prepOwner.signal);
  const second = client.prepareRead('/control/v1/notifications', new AbortController().signal);
  const cancelled = client.read('/control/v1/notifications', objectValue, pageOwner.signal).catch(() => null);
  const page = client.read('/control/v1/notifications', objectValue);
  pageOwner.abort();
  check(await cancelled === null && responses.length === 1, 'one cancelled consumer leaves the shared preparation available');
  responses[0]!(Response.json({ value: 1 }));
  check((await first as { value: number }).value === 1 && (await second as { value: number }).value === 1 &&
    (await page).value === 1, 'concurrent preparation and page reads share one successful response');

  const reportPath = '/control/v1/records/run/attempts/attempt/report';
  const reportA = client.read(reportPath, objectValue), reportB = client.read(reportPath, objectValue);
  check(responses.length === 2, 'concurrent reads of the same archived attempt share their report');
  responses[1]!(Response.json({ value: 2 }));
  await Promise.all([reportA, reportB]);
  const next = client.read(reportPath, objectValue);
  check(responses.length === 3, 'a later report read obtains a fresh response');
  responses[2]!(Response.json({ value: 3 })); await next;

  const stale = client.prepareRead('/control/v1/notifications', new AbortController().signal).catch(() => null);
  const current = client.read('/control/v1/notifications', objectValue);
  client.invalidatePrepared();
  responses[3]!(Response.json({ value: 'old' }));
  await stale;
  for (let index = 0; index < 20 && responses.length < 5; index++) await Promise.resolve();
  check(responses.length === 5, 'a newer event invalidates an in-flight prepared snapshot');
  responses[4]!(Response.json({ value: 'new' }));
  check((await current).value === 'new', 'the visible page retries against the new event generation');

  const invalid = client.prepareRead('/control/v1/notifications', new AbortController().signal, objectValue).catch(() => null);
  responses[5]!(Response.json('invalid')); await invalid;
  const recovered = client.prepareRead('/control/v1/notifications', new AbortController().signal, objectValue);
  responses[6]!(Response.json({ value: 'valid' }));
  check((await recovered).value === 'valid', 'invalid decoded preparation is removed before retry');
}

async function summaryPreparation(check: (value: unknown, name: string) => void): Promise<void> {
  const paths: string[] = [];
  const client = new ControlClient(location.origin, async input => {
    const url = new URL(String(input)); paths.push(url.pathname + url.search);
    return Response.json(url.pathname.endsWith('/days')
      ? { totals: {}, days: [{ date: '2026-11-03' }] } : { users: [], nextUserId: null });
  });
  const store = createStore(client);
  store.state.page = 'history';
  store.state.interface = { revision: 1, values: { theme: 'dark', locale: 'zh-CN', dashboardCards: ['history-duration'],
    pinnedSettings: [], collapsedSettings: [] }, operationId: '' };
  store.can = capability => capability === 'records.read';
  const completed = new Set<string>();
  try {
    await preparePages(store, new AbortController().signal, () => {}, () => {}, true, completed);
    check(paths.filter(path => path.includes('/summary/days?')).length === 1 &&
      paths.every(path => path.includes('/summary/days?') || path.includes('/summary/users?')),
      'history foreground preparation requests its own date range without the dashboard summary');
    if (innerWidth > 820) {
      const users = paths.find(path => path.includes('/summary/users?'))!;
      const before = paths.length;
      await client.read(users, objectValue);
      check(paths.length === before, 'the expanded date consumes its prepared user page');
    }
    await preparePages(store, new AbortController().signal, () => {}, () => {}, false, completed);
    check(paths.filter(path => path.includes('/summary/days?')).length === 2,
      'background preparation still warms the visible dashboard summary');
  } finally { store.dispose(); }
}

async function routePreparation(check: (value: unknown, name: string) => void): Promise<void> {
  for (const [revision, cards, expected] of [[1, ['status'], false], [1, ['history-counts'], true], [0, [], true]] as const) {
    const paths: string[] = [];
    const client = new ControlClient(location.origin, async input => {
      paths.push(new URL(String(input)).pathname);
      return Response.json({ totals: {}, days: [] });
    });
    const store = createStore(client);
    store.state.page = 'dashboard';
    store.state.interface = { revision, values: { theme: 'dark', locale: 'zh-CN', dashboardCards: [...cards],
      pinnedSettings: [], collapsedSettings: [] }, operationId: '' };
    store.can = capability => capability === 'records.read';
    try {
      await preparePages(store, new AbortController().signal, () => {}, () => {}, true);
      check(paths.includes('/control/v1/records/summary/days') === expected,
        'dashboard summary preparation follows the actual card layout');
    } finally { store.dispose(); }
  }
  for (const page of ['scripts', 'users', 'plugins'] as const) {
    const paths: string[] = [];
    const client = new ControlClient(location.origin, async input => {
      const path = new URL(String(input)).pathname; paths.push(path);
      return Response.json(path === '/control/v1/extensions' ? { hasReleaseTrust: true, items: [] } : null);
    });
    const store = createStore(client), completed = new Set<string>();
    store.state.page = page;
    store.can = capability => capability === 'extensions.read';
    try {
      await preparePages(store, new AbortController().signal, () => {}, () => {}, true, completed);
      check(paths.includes('/control/v1/extensions/catalog/stable') === (page === 'plugins'),
        'only the plugin page prepares its catalog in the foreground');
      await client.read('/control/v1/extensions', objectValue);
      await preparePages(store, new AbortController().signal, () => {}, () => {}, false, completed);
      check(paths.filter(path => path === '/control/v1/extensions/catalog/stable').length === 1,
        'background preparation retains one plugin catalog after the installed list is consumed');
    } finally { store.dispose(); clearExtensionIcons(); }
  }
}

async function preparationCancellation(check: (value: unknown, name: string) => void): Promise<void> {
  let requests = 0, transportAborted = false;
  const client = new ControlClient(location.origin, async (_input, init) => {
    requests++;
    return await new Promise<Response>((_resolve, reject) => {
      init!.signal!.addEventListener('abort', () => { transportAborted = true; reject(init!.signal!.reason); }, { once: true });
    });
  });
  const store = createStore(client), controller = new AbortController(), errors: unknown[] = [];
  store.state.page = 'dashboard';
  store.state.interface = { revision: 0, values: { theme: 'dark', locale: 'zh-CN', dashboardCards: [],
    pinnedSettings: [], collapsedSettings: [] }, operationId: '' };
  try {
    store.can = () => false;
    await preparePages(store, controller.signal, () => {}, error => errors.push(error), true);
    check(requests === 0, 'page preparation does not read data without its capability');
    store.can = capability => capability === 'records.read';
    const pending = preparePages(store, controller.signal, () => {}, error => errors.push(error), true)
      .then(() => false, error => error === controller.signal.reason);
    controller.abort();
    check(await pending && transportAborted && !errors.length,
      'cancelling foreground preparation aborts its transport without reporting a failure');
  } finally { controller.abort(); store.dispose(); }
}

async function eventRevision(check: (value: unknown, name: string) => void): Promise<void> {
  let revision = 0;
  const client = new ControlClient(location.origin, async () => Response.json({ value: ++revision }));
  const store = createStore(client);
  const snapshots = new HostSnapshots(client, store.state, {} as never);
  const event = { instanceId: 'instance', streamId: 'stream', sequence: 1, state: {} };
  try {
    snapshots.observe(event);
    await client.prepareRead('/control/v1/notifications', new AbortController().signal);
    snapshots.observe({ ...event, sequence: 2 });
    const next = await client.read('/control/v1/notifications', objectValue);
    check(next.value === 2 && revision === 2, 'a new event revision invalidates unconsumed prepared data');
  } finally { store.dispose(); }
}
