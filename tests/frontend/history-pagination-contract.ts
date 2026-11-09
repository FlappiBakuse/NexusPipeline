import { createApp, h, nextTick, ref } from 'vue';
import AttemptDetail from '../../apps/web/src/pages/AttemptDetail.vue';
import HistoryIndex from '../../apps/web/src/pages/HistoryIndex.vue';
import HistoryInvocations from '../../apps/web/src/pages/HistoryInvocations.vue';
import { ControlClient } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';
import type { AttemptFacts } from '../../apps/web/src/control/records';
import type { ActiveStep } from '../../apps/web/src/control/execution';
import type { HistoryLog } from '../../apps/web/src/pages/attempt-resources';

export async function historyPaginationContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const id = '10000000-0000-4000-8000-000000000001', digest = 'a'.repeat(64);
  const capture = (index: number) => `20000000-0000-4000-8000-${String(index).padStart(12, '0')}`;
  const log = (position: number, stepId: string, role: string, invocationOrdinal: number | null = null) => ({ position,
    key: { runId: id, attemptId: id, stepId, role, invocationOrdinal }, snapshotSha256: digest });
  const image = (position: number, stepId: string) => ({ position, outcome: 'unavailable', invocationOrdinal: 1,
    key: { runId: id, attemptId: id, stepId, captureId: capture(position + 1) },
    reasonCode: 'execution_capture_deadline', resultSha256: digest });
  const requests: string[] = [];
  const client = new ControlClient(location.origin, async input => {
    const url = new URL(String(input)); requests.push(url.pathname + url.search);
    if (url.pathname.endsWith('/report')) return Response.json({ operationId: id, runId: id, attemptId: id,
      ordinal: 1, phase: 'committed', canCancel: false, cancellationRequested: false, steps: [] });
    if (url.pathname.endsWith('/logs')) return Response.json(url.searchParams.get('afterPosition') === '-1'
      ? { items: [log(0, 'other', 'main'), log(1, 'wanted', 'main')], nextPosition: 1 }
      : { items: [log(2, 'wanted', 'postHook', 2)], nextPosition: null });
    if (url.pathname.endsWith('/images')) return Response.json(url.searchParams.get('afterPosition') === '-1'
      ? { items: [image(0, 'other'), image(1, 'wanted')], nextPosition: 1 }
      : { items: [image(2, 'wanted')], nextPosition: null });
    const role = url.pathname.split('/').at(-2)!;
    const ordinal = url.searchParams.get('invocationOrdinal');
    const key = { runId: id, attemptId: id, stepId: 'wanted', role, invocationOrdinal: ordinal ? Number(ordinal) : null };
    if (url.pathname.endsWith('/tail')) return Response.json({ description: { key },
      slices: [{ bytes: btoa(role + ' tail') }], prefixOmitted: false });
    if (url.pathname.endsWith('/description')) return Response.json({ key, snapshotSha256: digest,
      parts: [{ index: 0, source: 'standardOutput', fileSourceId: null, fileOffset: null, sizeBytes: 3 },
        { index: 1, source: 'standardOutput', fileSourceId: null, fileOffset: null, sizeBytes: 2 }] });
    if (url.pathname.endsWith('/parts/0')) return new Response(new Uint8Array([0x42, 0xe4, 0xbd]));
    if (url.pathname.endsWith('/parts/1')) return new Response(new Uint8Array([0xa0, 0x41]));
    throw Error('unexpected_history_request:' + url.pathname);
  });
  const owner = createStore(client), store = { ...owner, can: () => true };
  const facts: AttemptFacts = { runId: id, attemptId: id, ordinal: 1, engine: 'succeeded', business: 'succeeded',
    lifecycle: 'completed', recovery: 'restored', reasonCode: 'fixture' };
  const mount = document.createElement('section'); fixture.prepend(mount); mount.scrollIntoView();
  const app = createApp({ render: () => h(AttemptDetail, { store, runId: id, attemptId: id, visible: true,
    facts, stepIds: ['wanted'] }) });
  const wait = async (condition: () => boolean) => {
    const until = Date.now() + 5000;
    while (!condition() && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 20));
    check(condition(), 'history page reaches its requested result');
  };
  const button = (label: string) => [...mount.querySelectorAll<HTMLButtonElement>('button')]
    .find(element => element.textContent?.trim() === label);
  app.mount(mount);
  try {
    await wait(() => !!button('加载更多日志') && !!button('加载更多截图') &&
      !!mount.querySelector('.history-log-toggle') && mount.querySelectorAll('.history-image-unavailable').length === 1);
    check(!requests.some(path => /\/(logs|images)\?/.test(path) && new URL(path, location.origin).searchParams.get('afterPosition') !== '-1') &&
      !requests.some(path => path.includes('/tail?')), 'catalog pagination waits for user demand and collapsed logs do not read content');
    check(mount.querySelectorAll('.history-image-unavailable').length === 1,
      'the first page retains only the selected account’s screenshots');
    mount.querySelector<HTMLButtonElement>('.history-log-toggle')!.click();
    await wait(() => mount.querySelector('.history-log-preview pre')?.textContent === 'main tail');
    const tails = requests.filter(path => path.includes('/tail?')).length;
    button('加载更多日志')!.click();
    await wait(() => !button('加载更多日志'));
    check(mount.querySelector('.history-log-preview pre')?.textContent === 'main tail' &&
      requests.filter(path => path.includes('/tail?')).length === tails,
      'loading another page preserves the selected log without reading new tails');
    const logButtons = [...mount.querySelectorAll<HTMLButtonElement>('.history-log-disclosure nav button')];
    check(logButtons.length === 2 && !logButtons.some(item => item.textContent?.includes('other')) &&
      logButtons[1]!.textContent?.includes('第 2 次尝试'),
      'the next page retains the invocation identity within the selected account scope');
    logButtons[1]!.click();
    await wait(() => mount.querySelector('.history-log-preview pre')?.textContent === 'postHook tail');
    check(requests.some(path => path.includes('/postHook/tail?') && path.includes('invocationOrdinal=2')),
      'the selected paged log reads the correct invocation');
    button('加载更多截图')!.click();
    await wait(() => mount.querySelectorAll('.history-image-unavailable').length === 2);
    check(!button('加载更多截图'), 'the final screenshot page retains existing items and closes pagination');
    button('查看完整日志')!.click(); await nextTick();
    await wait(() => mount.querySelector('.log-window pre')?.textContent === 'B');
    const next = [...mount.querySelectorAll<HTMLButtonElement>('.log-window + .toolbar button')]
      .find(element => element.textContent?.trim() === '下一段');
    check(!!next, 'the full viewer opens the selected log with segment controls');
    next!.click();
    await wait(() => mount.querySelector('.log-window pre')?.textContent === '你A');
  } finally { app.unmount(); mount.remove(); owner.dispose(); }
  await scopeChangeContract(fixture, check);
  await invocationContract(fixture, check);
  await userPageContract(fixture, check);
}

async function scopeChangeContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const id = '10000000-0000-4000-8000-000000000001';
  const row = (position: number, stepId: string, role: string) => ({ position,
    key: { runId: id, attemptId: id, stepId, role, invocationOrdinal: null }, snapshotSha256: 'a'.repeat(64) });
  const pending: { resolve: (response: Response) => void; signal: AbortSignal }[] = [];
  const client = new ControlClient(location.origin, async (input, init) => {
    const url = new URL(String(input));
    if (url.pathname.endsWith('/report')) return Response.json({ operationId: id, runId: id, attemptId: id,
      ordinal: 1, phase: 'committed', canCancel: false, cancellationRequested: false, steps: [] });
    if (url.pathname.endsWith('/images')) return Response.json({ items: [], nextPosition: null });
    if (url.pathname.endsWith('/logs')) {
      if (url.searchParams.get('afterPosition') === '-1')
        return Response.json({ items: [row(0, 'first', 'main'), row(1, 'second', 'main')], nextPosition: 1 });
      return new Promise<Response>(resolve => pending.push({ resolve, signal: init!.signal! }));
    }
    if (url.pathname.endsWith('/tail')) {
      const pieces = url.pathname.split('/'), stepId = pieces.at(-3)!, role = pieces.at(-2)!;
      return Response.json({ description: { key: { runId: id, attemptId: id, stepId, role, invocationOrdinal: null } },
        slices: [{ bytes: btoa(stepId) }], prefixOmitted: false });
    }
    throw Error('unexpected_scope_request:' + url.pathname);
  });
  const owner = createStore(client), store = { ...owner, can: () => true };
  const facts: AttemptFacts = { runId: id, attemptId: id, ordinal: 1, engine: 'succeeded', business: 'succeeded',
    lifecycle: 'completed', recovery: 'restored', reasonCode: 'fixture' };
  const scope = ref(['first']), visible = ref(true), mount = document.createElement('section');
  fixture.prepend(mount); mount.scrollIntoView();
  const app = createApp({ render: () => h(AttemptDetail, { store, runId: id, attemptId: id, visible: visible.value,
    facts, stepIds: scope.value }) });
  const wait = async (condition: () => boolean) => {
    const until = Date.now() + 5000;
    while (!condition() && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 20));
    check(condition(), 'account scope reaches its requested state');
  };
  const more = () => [...mount.querySelectorAll<HTMLButtonElement>('button')]
    .find(element => element.textContent?.trim() === '加载更多日志');
  const select = async (step: string) => {
    await wait(() => !!mount.querySelector('.history-log-toggle') && !!more() && !more()!.disabled);
    mount.querySelector<HTMLButtonElement>('.history-log-toggle')!.click();
    await wait(() => mount.querySelector('.history-log-preview pre')?.textContent === step);
  };
  app.mount(mount);
  try {
    await wait(() => !!more() && !more()!.disabled);
    more()!.click(); await wait(() => pending.length === 1);
    visible.value = false; await nextTick();
    scope.value = ['second']; visible.value = true; await nextTick();
    await select('second');
    pending[0]!.resolve(Response.json({ items: [row(2, 'second', 'postHook')], nextPosition: null }));
    await new Promise(resolve => setTimeout(resolve, 0));
    check(pending[0]!.signal.aborted && !mount.querySelector('.history-log-disclosure nav') && !!more(),
      'a cancelled older page cannot append to a different account scope');

    more()!.click(); await wait(() => pending.length === 2);
    scope.value = ['first']; await nextTick(); await select('first');
    pending[1]!.resolve(Response.json({ code: 'old_scope_failure' }, { status: 503 }));
    await new Promise(resolve => setTimeout(resolve, 0));
    check(pending[1]!.signal.aborted && ![...mount.querySelectorAll<HTMLElement>('[role="alert"]')]
      .some(element => getComputedStyle(element).display !== 'none'),
      'an error from the previous scope cannot replace the new account state');
  } finally {
    for (const request of pending) request.resolve(Response.json({ items: [], nextPosition: null }));
    app.unmount(); mount.remove(); owner.dispose();
  }
}

async function invocationContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const id = '10000000-0000-4000-8000-000000000001', requests: string[] = [];
  const client = new ControlClient(location.origin, async input => {
    const url = new URL(String(input)); requests.push(url.pathname + url.search);
    const role = url.pathname.split('/').at(-2)!, ordinal = Number(url.searchParams.get('invocationOrdinal'));
    return Response.json({ description: { key: { runId: id, attemptId: id, stepId: 'wanted', role,
      invocationOrdinal: ordinal } }, slices: [{ bytes: btoa(role + ':' + ordinal) }], prefixOmitted: false });
  });
  const owner = createStore(client), store = { ...owner, can: () => true };
  const logs = ref<HistoryLog[]>([{ key: { stepId: 'wanted', role: 'main', invocationOrdinal: 1 } },
    { key: { stepId: 'wanted', role: 'preHook' } }]);
  const invocation = (ordinal: number) => ({ ordinal, engine: 'Succeeded', business: 'Succeeded',
    reasonCode: 'fixture', startedAt: null, endedAt: null, durationMilliseconds: null });
  const steps: ActiveStep[] = [{ stepId: 'wanted', scriptId: 'script', userId: null, bindingId: null,
    dependsOn: [], phase: 'collected', tasks: [], logRoles: ['main', 'preHook'],
    invocations: [invocation(1), invocation(2)] }];
  const mount = document.createElement('section'); fixture.prepend(mount);
  const app = createApp({ render: () => h(HistoryInvocations, { store, runId: id, attemptId: id, visible: true,
    steps, logs: logs.value, images: [], logsReady: true, imagesReady: false }) });
  const wait = async (condition: () => boolean) => {
    const until = Date.now() + 5000;
    while (!condition() && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 20));
    check(condition(), 'invocation log reaches its requested state');
  };
  app.mount(mount);
  try {
    await nextTick();
    const cards = () => [...mount.querySelectorAll<HTMLElement>('.history-attempt-card')];
    cards()[0]!.querySelector<HTMLButtonElement>('.history-log-toggle')!.click();
    cards()[1]!.querySelector<HTMLButtonElement>('.history-log-toggle')!.click();
    await wait(() => cards()[0]!.querySelector('.history-log-preview pre')?.textContent === 'main:1' &&
      cards()[1]!.querySelector('.history-log-preview pre')?.textContent === 'preHook:2');
    logs.value = [...logs.value, { key: { stepId: 'wanted', role: 'main', invocationOrdinal: 2 } }];
    await nextTick();
    const first = cards()[0]!.querySelectorAll('.history-log-disclosure nav button');
    const second = [...cards()[1]!.querySelectorAll<HTMLButtonElement>('.history-log-disclosure nav button')];
    check(first.length === 2 && second.length === 2 &&
      cards()[0]!.querySelector('.history-log-preview pre')?.textContent === 'main:1' &&
      cards()[1]!.querySelector('.history-log-preview pre')?.textContent === 'preHook:2',
      'additional invocation-specific entries preserve both selected logs and filter each invocation');
    second.find(button => button.textContent?.includes('main'))!.click();
    await wait(() => cards()[1]!.querySelector('.history-log-preview pre')?.textContent === 'main:2');
    check(requests.some(path => path.includes('/main/tail?') && path.includes('invocationOrdinal=2')),
      'aggregated log entries remain usable for each invocation and specific entries use their own ordinal');
  } finally { app.unmount(); mount.remove(); owner.dispose(); }
}

async function userPageContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const paths: string[] = [];
  const client = new ControlClient(location.origin, async input => {
    const url = new URL(String(input)); paths.push(url.pathname + url.search);
    return Response.json({ users: [{ userId: null, totals: { runs: 2 } }], nextUserId: null });
  });
  const store = createStore(client), mount = document.createElement('section');
  fixture.prepend(mount); mount.scrollIntoView();
  const app = createApp({ render: () => h(HistoryIndex, { store, days: [{ date: '2026-11-03' }], visible: true,
    selectedDate: '', selectedUser: '', business: '', lifecycle: '', loading: false, error: '' }) });
  app.mount(mount);
  try {
    await nextTick();
    const toggle = mount.querySelector<HTMLButtonElement>('.date-heading')!;
    if (toggle.getAttribute('aria-expanded') !== 'true') toggle.click();
    const until = Date.now() + 5000;
    while (!mount.querySelector('.history-user') && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 20));
    check(mount.querySelector('.history-user')?.textContent?.includes('未指定用户') && paths.some(path => path.includes('/summary/users?')),
      'expanded dates load and display their account page through the user summary endpoint');
  } finally { app.unmount(); mount.remove(); store.dispose(); }
}
