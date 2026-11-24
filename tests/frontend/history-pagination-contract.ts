import { createApp, h, nextTick, ref } from 'vue';
import AttemptDetail from '../../apps/web/src/pages/AttemptDetail.vue';
import HistoryIndex from '../../apps/web/src/pages/HistoryIndex.vue';
import HistoryInvocations from '../../apps/web/src/pages/HistoryInvocations.vue';
import AttemptLogs from '../../apps/web/src/pages/AttemptLogs.vue';
import { ControlClient } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';
import type { AttemptFacts, HistoryImage } from '../../apps/web/src/control/records';
import type { ActiveStep } from '../../apps/web/src/control/execution';
import type { HistoryLog } from '../../apps/web/src/pages/attempt-resources';
import type { UiElement } from '@nexuspipeline/extension-sdk';
import { showsHeightMotion } from './disclosure-motion';

const fullMainLog = 'start\n' + 'middle line\n'.repeat(250) + 'x'.repeat(70000) + '\n你好\nend';
const postHookLog = 'OE你\nXY好你G�S�';
const encoder = new TextEncoder();
type LogFixturePart = { bytes: Uint8Array; source: 'standardOutput' | 'standardError' | 'file';
  fileSourceId?: number; fileOffset?: number };
const interleavedParts: LogFixturePart[] = [
  { bytes: Uint8Array.of(0x4f, 0xe4, 0xbd), source: 'standardOutput' },
  { bytes: encoder.encode('E'), source: 'standardError' },
  { bytes: Uint8Array.of(0xa0, 0x0a), source: 'standardOutput' },
  { bytes: Uint8Array.of(0x58, 0xe5), source: 'file', fileSourceId: 0, fileOffset: 0 },
  { bytes: Uint8Array.of(0x59, 0xe4, 0xbd), source: 'file', fileSourceId: 1, fileOffset: 0 },
  { bytes: Uint8Array.of(0xa5, 0xbd), source: 'file', fileSourceId: 0, fileOffset: 2 },
  { bytes: Uint8Array.of(0xa0), source: 'file', fileSourceId: 1, fileOffset: 3 },
  { bytes: Uint8Array.of(0x47, 0xe4, 0xbd), source: 'file', fileSourceId: 0, fileOffset: 4 },
  { bytes: encoder.encode('S'), source: 'standardError' },
  { bytes: Uint8Array.of(0xa0), source: 'file', fileSourceId: 0, fileOffset: 10 }
];

function historyLogResponse(url: URL, key: Record<string, unknown>, digest: string, text: string | LogFixturePart[]): Response {
  const discardedBytes = typeof text === 'string' ? 0 : 3;
  const fixtures: LogFixturePart[] = [];
  if (typeof text === 'string') {
    const bytes = encoder.encode(text);
    for (let offset = 0; offset < bytes.length; offset += 65536)
      fixtures.push({ bytes: bytes.subarray(offset, offset + 65536), source: 'standardOutput' });
  } else fixtures.push(...text);
  let recordedBytes = 0;
  const parts = fixtures.map(({ bytes, source, fileSourceId = null, fileOffset = null }, index) => {
    const observedOffset = recordedBytes + (index === fixtures.length - 1 ? discardedBytes : 0);
    recordedBytes += bytes.length;
    return { index, observedOffset, source, fileSourceId, fileOffset, sizeBytes: bytes.length,
      sha256: digest, contentAvailability: 'available' };
  });
  if (url.pathname.endsWith('/description')) return Response.json({ key, snapshotSha256: digest, recordedBytes, parts,
    observedBytes: recordedBytes + discardedBytes, discardedBytes, isComplete: discardedBytes === 0,
    contentAvailability: 'available' });
  if (url.pathname.endsWith('/original') && url.searchParams.get('snapshotSha256') === digest) {
    const bytes = new Uint8Array(recordedBytes);
    let offset = 0;
    for (const part of fixtures) {
      bytes.set(part.bytes, offset);
      offset += part.bytes.length;
    }
    return new Response(new ReadableStream({
      start(controller) {
        const split = typeof text === 'string' ? Math.max(0, bytes.length - 9) : 2;
        controller.enqueue(bytes.slice(0, split));
        controller.enqueue(bytes.slice(split));
        controller.close();
      }
    }));
  }
  throw Error('unexpected_history_log_request:' + url.pathname);
}

export async function historyPaginationContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const id = '10000000-0000-4000-8000-000000000001', digest = 'a'.repeat(64);
  const capture = (index: number) => `20000000-0000-4000-8000-${String(index).padStart(12, '0')}`;
  const log = (position: number, stepId: string, role: string, invocationOrdinal: number | null = null) => ({ position,
    key: { runId: id, attemptId: id, stepId, role, invocationOrdinal }, snapshotSha256: digest });
  const image = (position: number, stepId: string) => ({ position, outcome: 'unavailable', invocationOrdinal: 1,
    key: { runId: id, attemptId: id, stepId, captureId: capture(position + 1) },
    reasonCode: 'execution_capture_deadline', resultSha256: digest });
  const requests: string[] = [];
  let finishOriginal: (() => void) | undefined;
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
    const response = historyLogResponse(url, key, digest, role === 'main' ? fullMainLog : interleavedParts);
    if (role === 'main' && url.pathname.endsWith('/original'))
      return new Promise<Response>(resolve => { finishOriginal = () => resolve(response); });
    return response;
  });
  const owner = createStore(client), store = { ...owner, can: () => true };
  const facts: AttemptFacts = { runId: id, attemptId: id, ordinal: 1, engine: 'succeeded', business: 'succeeded',
    lifecycle: 'completed', recovery: 'restored', reasonCode: 'fixture' };
  const mount = document.createElement('section'); fixture.prepend(mount); mount.scrollIntoView();
  const app = createApp({ render: () => h(AttemptDetail, { store, runId: id, attemptId: id, visible: true,
    facts, stepIds: ['wanted'] }) });
  const wait = async (condition: () => boolean, name = 'history page reaches its requested result') => {
    const until = Date.now() + 5000;
    while (!condition() && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 20));
    check(condition(), name);
  };
  const button = (label: string) => [...mount.querySelectorAll<HTMLButtonElement>('button')]
    .find(element => element.textContent?.trim() === label);
  app.mount(mount);
  try {
    await wait(() => !!button('加载更多日志') && !!button('加载更多截图') &&
      !!mount.querySelector('.history-log-toggle') && mount.querySelectorAll('.history-image-unavailable').length === 1);
    check(!requests.some(path => /\/(logs|images)\?/.test(path) && new URL(path, location.origin).searchParams.get('afterPosition') !== '-1') &&
      !requests.some(path => /\/(tail|description|original)\b/.test(path)), 'catalog pagination waits for user demand and collapsed logs do not read content');
    check(mount.querySelectorAll('.history-image-unavailable').length === 1,
      'the first page retains only the selected account’s screenshots');
    const trigger = mount.querySelector<HTMLElement>('.history-log-toggle')!;
    trigger.focus();
    trigger.click();
    await wait(() => !!finishOriginal);
    const spinner = trigger.querySelector<UiElement<'nexus-spinner'>>('nexus-spinner');
    check(trigger.matches('button') && spinner?.label === '正在读取脚本日志…' &&
      !trigger.textContent?.includes('点击展开') && !mount.querySelector('.history-log-preview'),
      'a pending log read replaces the disclosure label with progress inside the original button');
    trigger.click();
    check(requests.filter(path => path.includes('/original?')).length === 1, 'a busy log trigger cannot start a second read');
    const output = () => mount.querySelector<HTMLElement>('.history-log-preview pre');
    const revealed = await showsHeightMotion([
      () => mount.querySelector('.history-log-disclosure .disclosure-clip')?.getBoundingClientRect().height ?? 0
    ], finishOriginal!, () => output()?.textContent === fullMainLog);
    check(revealed, 'the loaded log grows through an intermediate height before displaying its complete content');
    check(!trigger.isConnected && document.activeElement === output()?.closest('.history-log-content'),
      'the completed read removes the disclosure button and moves its focus to the displayed log');
    const viewport = output()!.parentElement!.shadowRoot!.querySelector<HTMLElement>('.viewport')!;
    viewport.scrollTop = viewport.scrollHeight;
    check(viewport.scrollWidth <= viewport.clientWidth + 1 && viewport.scrollTop > 0 &&
      viewport.scrollTop + viewport.clientHeight >= viewport.scrollHeight - 1 && output()?.textContent?.endsWith('你好\nend'),
      'the selected complete log fits horizontally and scrolls to its final original text');
    check(!button('查看完整日志') && !mount.querySelector('nexus-modal'),
      'the complete log is available without opening a separate viewer');
    const originals = requests.filter(path => path.includes('/original?')).length;
    button('加载更多日志')!.click();
    await wait(() => !button('加载更多日志'));
    check(output()?.textContent === fullMainLog && requests.filter(path => path.includes('/original?')).length === originals,
      'loading another catalog page preserves the already displayed complete log');
    const logButtons = [...mount.querySelectorAll<HTMLButtonElement>('.history-log-disclosure nav button')];
    check(logButtons.length === 2 && !logButtons.some(item => item.textContent?.includes('other')) &&
      logButtons[1]!.textContent?.includes('第 2 次尝试'),
      'the next page retains the invocation identity within the selected account scope');
    logButtons[1]!.click();
    await wait(() => output()?.textContent === postHookLog,
      'UTF-8 continuity follows the log source and contiguous file epochs across transport reads');
    check(requests.some(path => path.includes('/postHook/original?') && path.includes('invocationOrdinal=2')),
      'the selected paged log reads the correct invocation');
    button('加载更多截图')!.click();
    await wait(() => mount.querySelectorAll('.history-image-unavailable').length === 2);
    check(!button('加载更多截图'), 'the final screenshot page retains existing items and closes pagination');
  } finally { finishOriginal?.(); app.unmount(); mount.remove(); owner.dispose(); }
  await scopeChangeContract(fixture, check);
  await invocationContract(fixture, check);
  await largeTranscriptContract(fixture, check);
  await userPageContract(fixture, check);
}

async function largeTranscriptContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const id = '10000000-0000-4000-8000-000000000001', key = { stepId: 'long', role: 'main' };
  const contents = '0123456789 你好 🚀\n'.repeat(160000) + 'last line';
  const store = createStore(new ControlClient(location.origin, async input =>
    historyLogResponse(new URL(String(input)), { runId: id, attemptId: id, ...key }, 'e'.repeat(64), contents)));
  const holder = document.createElement('section'); fixture.append(holder);
  const app = createApp({ render: () => h(AttemptLogs, { store, runId: id, attemptId: id, visible: true, logs: [{ key }] }) });
  app.mount(holder);
  try {
    holder.querySelector<HTMLButtonElement>('.history-log-toggle')!.click();
    const until = Date.now() + 5000;
    const shown = () => holder.querySelector<HTMLPreElement>('.history-log-preview pre');
    while (!shown() && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 20));
    check(shown()?.textContent === contents, 'the visible transcript retains every original decoded character across its internal segments');
    const sizes = [...shown()!.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent!.length);
    check(sizes.length > 1 && Math.max(...sizes) <= 131072,
      `large logs avoid materializing one giant rendered text node (${sizes.length} nodes, largest ${Math.max(...sizes)} characters)`);
  } finally { app.unmount(); holder.remove(); store.dispose(); }
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
    const pieces = url.pathname.split('/'), stepId = pieces.at(-3)!, role = pieces.at(-2)!;
    return historyLogResponse(url, { runId: id, attemptId: id, stepId, role, invocationOrdinal: null }, 'a'.repeat(64), stepId);
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
    mount.querySelector<HTMLElement>('.history-log-toggle')!.click();
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
    return historyLogResponse(url, { runId: id, attemptId: id, stepId: 'wanted', role, invocationOrdinal: ordinal },
      'a'.repeat(64), role + ':' + ordinal);
  });
  const owner = createStore(client), store = { ...owner, can: () => true };
  let logVisits = 0, imageVisits = 0;
  const logs = ref<HistoryLog[]>([{ key: { stepId: 'wanted', role: 'main', invocationOrdinal: 1 } },
    { key: { stepId: 'wanted', role: 'preHook' } },
    ...Array.from({ length: 32 }, (_, index) => ({ key: { get stepId() { logVisits++; return 'other-' + index; }, role: 'main' } }))]);
  const unavailable = { outcome: 'unavailable', invocationOrdinal: 2, reasonCode: 'execution_capture_deadline', resultSha256: 'b'.repeat(64) } as const;
  const images: HistoryImage[] = [{ ...unavailable, key: { captureId: 'capture-wanted', stepId: 'wanted' } },
    ...Array.from({ length: 32 }, (_, index) => ({ ...unavailable,
      key: { captureId: 'capture-' + index, get stepId() { imageVisits++; return 'other-' + index; } } }))];
  const invocation = (ordinal: number) => ({ ordinal, engine: 'Succeeded', business: 'Succeeded',
    reasonCode: 'fixture', startedAt: null, endedAt: null, durationMilliseconds: null });
  const steps: ActiveStep[] = [{ stepId: 'wanted', scriptId: 'script', userId: null, bindingId: null,
    dependsOn: [], phase: 'collected', tasks: [], logRoles: ['main', 'preHook'],
    invocations: Array.from({ length: 10 }, (_, index) => invocation(index + 1)) }];
  const mount = document.createElement('section'); fixture.prepend(mount);
  const app = createApp({ render: () => h(HistoryInvocations, { store, runId: id, attemptId: id, visible: true,
    steps, stepNames: [{ stepId: 'wanted', scriptName: 'Historical Script' }], logs: logs.value, images,
    logsReady: true, imagesReady: true }) });
  const wait = async (condition: () => boolean) => {
    const until = Date.now() + 5000;
    while (!condition() && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 20));
    check(condition(), 'invocation log reaches its requested state');
  };
  app.mount(mount);
  try {
    await nextTick();
    const cards = () => [...mount.querySelectorAll<HTMLElement>('.history-attempt-card')];
    check(logVisits === 32 && imageVisits === 32, `history scans unrelated logs and screenshots once for ten invocations (${logVisits}/${imageVisits})`);
    check(!cards()[0]!.querySelector('.history-image-unavailable') && cards()[1]!.querySelector('.history-image-unavailable'),
      'an unavailable screenshot appears only under its recorded invocation');
    check(mount.querySelector('section')?.getAttribute('aria-label') === 'Historical Script' &&
      !mount.innerText.includes('Historical Script'), 'the invocation section retains its accessible name without repeating the script title');
    cards()[0]!.querySelector<HTMLElement>('.history-log-toggle')!.click();
    cards()[1]!.querySelector<HTMLElement>('.history-log-toggle')!.click();
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
    check(requests.some(path => path.includes('/main/original?') && path.includes('invocationOrdinal=2')),
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
