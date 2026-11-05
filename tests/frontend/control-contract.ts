import { ControlClient, ControlFailure, objectValue } from '../../apps/web/src/control/client.js';
import { EventFeed, type EventSnapshot } from '../../apps/web/src/control/event-feed.js';
import { MediaPool, type ImageLease } from '../../apps/web/src/control/media-pool.js';
import { settings } from '../../apps/web/src/control/models.js';
import { valueSelector } from '../../apps/web/src/pages/value-selector.js';
import { executionCommandContract } from './execution-command-contract.js';
import { liveLogContract } from './live-log-contract.js';
import { completionCommands } from './completion-command-contract.js';
import { adapterResults } from './adapter-result-contract.js';

function assert(condition: unknown, message: string, checks: string[]): asserts condition {
  if (!condition) throw new Error(message);
  checks.push(message);
}

async function waitFor(condition: () => boolean, message: string, timeoutMs = 6000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!condition()) {
    if (Date.now() >= deadline) throw new Error('Timed out waiting for ' + message);
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}

function eventFrame(value: unknown): string { return 'data: ' + JSON.stringify({ format:'nexus.event', version:1, kind:'state',
  topic:'host.state',subject:{type:'instance',id:'instance-a'}, ...objectValue(value) }) + '\n\n'; }

function imageResponse(): Response {
  const bytes = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC'),
    value => value.charCodeAt(0));
  return new Response(bytes, { headers: { 'Content-Type': 'image/png' } });
}

interface HeldResponse { path: string; resolve(response: Response): void }

async function maintenanceExitContract(checks: string[]): Promise<void> {
  const held: HeldResponse[] = [];
  const client = new ControlClient('http://control.test', async input => await new Promise<Response>(resolve => {
    held.push({ path: new URL(String(input)).pathname, resolve });
  }));
  const id = '70000000-0000-4000-8000-000000000001';
  const restarting = client.restartHost(id, objectValue);
  assert(client.pendingWrites === 1 && client.canExitMaintenance(),
    'a held restart response allows its own signed maintenance exit while remaining a pending write', checks);
  let duplicate: unknown;
  try { await client.restartHost(id, objectValue); } catch (error) { duplicate = error; }
  assert(duplicate instanceof Error && held.length === 1,
    'a second restart cannot replace the pending restart or issue another request', checks);
  const ordinary = client.write('/control/v1/automation/change', { operationId: 'ordinary' }, objectValue);
  assert(Number(client.pendingWrites) === 2 && !client.canExitMaintenance(),
    'an ordinary pending write still prevents maintenance exit during restart', checks);
  held[1]!.resolve(jsonResponse({ saved: true }));
  await ordinary;
  assert(Number(client.pendingWrites) === 1 && client.canExitMaintenance(),
    'finishing the ordinary write permits the waiting restart handoff', checks);
  let releaseSecret!: () => void;
  const secret = client.trackExternalWrite(() => new Promise<void>(resolve => { releaseSecret = resolve; }));
  assert(Number(client.pendingWrites) === 2 && !client.canExitMaintenance(),
    'a held raw secret write prevents maintenance exit during restart', checks);
  releaseSecret();
  await secret;
  assert(Number(client.pendingWrites) === 1 && client.canExitMaintenance(),
    'settling the raw secret write restores only the restart exception', checks);
  held[0]!.resolve(jsonResponse({ state: 'handoff' }, 202));
  await restarting;
  assert(Number(client.pendingWrites) === 0 && client.canExitMaintenance(),
    'a completed restart releases all exit guards', checks);
  const generic = client.write('/control/v1/maintenance/restart/' + id, null, objectValue, id);
  assert(Number(client.pendingWrites) === 1 && !client.canExitMaintenance(),
    'a generic write cannot exclude itself merely by using a maintenance URL', checks);
  held[2]!.resolve(jsonResponse({ failed: true }, 409));
  await generic.catch(() => {});
  const offline = new ControlClient('http://control.test', async () => { throw new TypeError('offline'); });
  let unknown: unknown;
  try { await offline.restartHost(id, objectValue); } catch (error) { unknown = error; }
  assert(unknown instanceof ControlFailure && unknown.uncertain && unknown.operationId === id
    && offline.pendingWrites === 0 && offline.canExitMaintenance(),
  'a lost restart response preserves its uncertain operation without replay or a stuck guard', checks);
  let rejectSecret!: (error: unknown) => void;
  const uncertainSecret = client.trackExternalWrite(() => new Promise<void>((_, reject) => { rejectSecret = reject; }));
  assert(Number(client.pendingWrites) === 1 && !client.canExitMaintenance(),
    'a held raw secret write blocks ordinary maintenance preparation', checks);
  rejectSecret(new ControlFailure(0, 'write_outcome_unknown', undefined, id, true));
  let secretOutcome: unknown;
  try { await uncertainSecret; } catch (error) { secretOutcome = error; }
  assert(secretOutcome instanceof ControlFailure && secretOutcome.uncertain && secretOutcome.operationId === id
    && Number(client.pendingWrites) === 0 && client.canExitMaintenance(),
    'uncertain raw secret settlement preserves operation identity and releases its exit guard', checks);
}

async function mediaPoolContract(checks: string[]): Promise<void> {
  const originalCreate = URL.createObjectURL;
  const originalRevoke = URL.revokeObjectURL;
  const liveUrls = new Set<string>();
  const createdUrls: string[] = [];
  const revokedUrls: string[] = [];
  URL.createObjectURL = value => {
    const url = originalCreate.call(URL, value);
    liveUrls.add(url); createdUrls.push(url);
    return url;
  };
  URL.revokeObjectURL = url => {
    liveUrls.delete(url); revokedUrls.push(url); originalRevoke.call(URL, url);
  };

  try {
    const held: HeldResponse[] = [];
    const calls = new Map<string, number>();
    let active = 0;
    let maximumActive = 0;
    let canceled = 0;
    const client = new ControlClient(location.origin, async (input, init) => {
      const path = new URL(String(input)).pathname;
      calls.set(path, (calls.get(path) ?? 0) + 1);
      active++; maximumActive = Math.max(maximumActive, active);
      return await new Promise<Response>((resolve, reject) => {
        let finished = false;
        const signal = init?.signal;
        const finish = (): boolean => {
          if (finished) return false;
          finished = true; active--;
          signal?.removeEventListener('abort', abort);
          return true;
        };
        const abort = (): void => {
          if (!finish()) return;
          canceled++;
          reject(signal?.reason ?? new DOMException('cancelled', 'AbortError'));
        };
        if (signal?.aborted) { abort(); return; }
        signal?.addEventListener('abort', abort, { once: true });
        held.push({ path, resolve: response => { if (finish()) resolve(response); } });
      });
    });

    const pool = new MediaPool(client);
    const owners = Array.from({ length: 5 }, () => new AbortController());
    const paths = owners.map((_, index) => `/control/v1/media/${index}/frame`);
    const pending = paths.map((path, index) => pool.acquire(path, owners[index]!.signal));
    await waitFor(() => held.length === 4, 'four active image downloads');
    assert(maximumActive === 4 && held.length === 4,
      'media downloads stop at four active requests while later images wait', checks);

    const latePath = '/control/v1/media/late/frame';
    const lateOwner = new AbortController();
    let latePending: Promise<ImageLease> | undefined;
    held.find(request => request.path === paths[0])!.resolve(imageResponse());
    const first = await pending[0]!;
    latePending = pool.acquire(latePath, lateOwner.signal);
    await waitFor(() => held.some(request => request.path === paths[4]), 'fifth image after one slot frees');
    await new Promise(resolve => setTimeout(resolve, 10));
    const lateIndex = held.findIndex(request => request.path === latePath);
    const queuedIndex = held.findIndex(request => request.path === paths[4]);
    const exceededReservedSlot = maximumActive > 4 || (lateIndex >= 0 && lateIndex < queuedIndex);
    for (const path of [...paths.slice(1), latePath]) {
      await waitFor(() => held.some(request => request.path === path), 'queued image ' + path);
      held.find(request => request.path === path)!.resolve(imageResponse());
    }
    await waitFor(() => latePending !== undefined, 'late image request registration');
    const leases = [first, ...await Promise.all([...pending.slice(1), latePending!])];
    leases.forEach(lease => lease.release());
    assert(!exceededReservedSlot && maximumActive === 4,
      'a new image cannot steal a released slot from an earlier waiter or exceed four downloads', checks);
    assert(liveUrls.size === 0 && createdUrls.length === 0 && revokedUrls.length === 0 && pool.metrics.encodedBytes === 0,
      'all six completed Blob leases release their budget without creating unused object URLs', checks);

    const duplicatePath = '/control/v1/media/shared/frame';
    const duplicatePool = new MediaPool(client);
    const duplicateOwners = [new AbortController(), new AbortController()];
    const duplicateLeases = duplicateOwners.map(owner => duplicatePool.acquire(duplicatePath, owner.signal));
    await waitFor(() => held.some(request => request.path === duplicatePath), 'shared image request');
    assert(calls.get(duplicatePath) === 1,
      'simultaneous leases for one image share a single HTTP request', checks);
    held.find(request => request.path === duplicatePath)!.resolve(imageResponse());
    const shared = await Promise.all(duplicateLeases);
    assert(shared[0]!.blob === shared[1]!.blob && duplicatePool.metrics.encodedBytes > 0,
      'deduplicated image owners receive the same Blob', checks);
    shared[0]!.release();
    assert(duplicatePool.metrics.encodedBytes > 0, 'one release keeps the shared image alive for its remaining owner', checks);
    shared[1]!.release();
    assert(duplicatePool.metrics.encodedBytes === 0 && liveUrls.size === 0, 'the final shared-image release returns its byte budget', checks);

    const canceledPath = '/control/v1/media/canceled/frame';
    const cancellationPool = new MediaPool(client);
    const cancelOwners = [new AbortController(), new AbortController()];
    const outcomes = cancelOwners.map(owner => cancellationPool.acquire(canceledPath, owner.signal)
      .then(value => ({ value }), error => ({ error })));
    await waitFor(() => held.some(request => request.path === canceledPath), 'cancelable image request');
    const createdBeforeCancel = createdUrls.length;
    cancelOwners[0]!.abort();
    const firstOutcome = await outcomes[0]!;
    assert('error' in firstOutcome && firstOutcome.error instanceof DOMException
      && firstOutcome.error.name === 'AbortError' && canceled === 0,
    'canceling one owner leaves the shared request alive for its remaining owner', checks);
    cancelOwners[1]!.abort();
    const lastOutcome = await outcomes[1]!;
    await waitFor(() => canceled === 1, 'underlying image request cancellation');
    assert('error' in lastOutcome && lastOutcome.error instanceof DOMException
      && lastOutcome.error.name === 'AbortError' && createdUrls.length === createdBeforeCancel
      && liveUrls.size === 0,
    'canceling the final owner aborts the request without leaving an object URL', checks);
  } finally {
    URL.createObjectURL = originalCreate;
    URL.revokeObjectURL = originalRevoke;
    for (const url of liveUrls) originalRevoke.call(URL, url);
  }
}

export async function runControlContract(): Promise<{ checks: string[]; checkCount: number }> {
  const checks: string[] = [];
  const sourceDocument = { '0': 'a', list: [{ '0': 'b' }], 'a/b': { '~': 'c' }, '': 'empty' };
  assert(JSON.stringify(valueSelector(sourceDocument, '/0')) === '["0"]'
    && JSON.stringify(valueSelector(sourceDocument, '/list/0/0')) === '["list",0,"0"]'
    && JSON.stringify(valueSelector(sourceDocument, '/a~1b/~0')) === '["a/b","~"]'
    && JSON.stringify(valueSelector(sourceDocument, '/')) === '[""]',
    'typed selectors distinguish numeric object keys, array positions and escaped property names', checks);
  const savedValues = { autoStart: true, desktop: { mode: 'browser', minimizeToTray: true, openOnStartup: false },
    service: { webPort: 0, allowRemoteAccess: false, mcpEnabled: false, mcpWriteEnabled: false, mcpPort: 18291 },
    proxy: { mode: 'none', url: null, userName: '' }, allowConfigRepair: false, historyRetentionDays: 30,
    logLevel: 'info', locale: 'zhCn', extensions: [], updates: { checkEnabled: false, autoApplyEnabled: false,
      pluginAutoUpdateEnabled: false, channel: 'stable', sourceUrl: null, pluginSource: 'develop' },
    automaticRetry: { enabled: false, backoffSeconds: 5 } };
  const settingsResponse = { saved: { revision: 3, values: savedValues }, effective: { ...savedValues, autoStart: false },
    restartReasons: [], startupEffect: { operationId: '70000000-0000-4000-8000-000000000003',
      settingsRevision: 3, enable: true, state: 'quarantined' } };
  const startup = settings(settingsResponse);
  assert(startup.saved.values.autoStart && !startup.effective.autoStart && startup.startupEffect?.state === 'quarantined'
    && startup.startupEffect.operationId === settingsResponse.startupEffect.operationId && startup.startupEffect.settingsRevision === 3,
  'a quarantined startup intent preserves the saved request, actual disabled state and original operation identity', checks);
  let invalidStartupRejected = false;
  try { settings({ ...settingsResponse, startupEffect: { ...settingsResponse.startupEffect, state: 'retry' } }); }
  catch (error) { invalidStartupRejected = error instanceof TypeError; }
  assert(invalidStartupRejected, 'an unknown startup effect state is rejected instead of being presented as applied', checks);
  await maintenanceExitContract(checks);

  let writeCalls = 0;
  const unavailable = new ControlClient('http://control.test', async () => {
    writeCalls++;
    throw new TypeError('offline');
  });
  let writeFailure: unknown;
  try {
    await unavailable.write('/control/v1/execution/start', { runId: 'run-a' }, value => value, 'operation-a');
  } catch (error) { writeFailure = error; }
  assert(writeFailure instanceof ControlFailure && writeFailure.code === 'write_outcome_unknown'
    && writeFailure.operationId === 'operation-a' && writeFailure.uncertain,
  'network loss keeps write outcome unknown and preserves its operation identity', checks);
  assert(writeCalls === 1 && unavailable.pendingWrites === 0,
    'unknown writes are never replayed and release the pending-write count', checks);
  const malformed = new ControlClient('http://control.test', async () => new Response('', { status: 200 }));
  let invalidRead: unknown;
  try { await malformed.read('/control/v1/extensions', objectValue); } catch (error) { invalidRead = error; }
  assert(invalidRead instanceof ControlFailure && invalidRead.code === 'control_response_invalid' && !invalidRead.uncertain,
    'an empty JSON success is a response-contract failure rather than a lost connection', checks);
  let invalidWrite: unknown;
  try { await malformed.write('/control/v1/automation/change', {}, objectValue, 'malformed-operation'); }
  catch (error) { invalidWrite = error; }
  assert(invalidWrite instanceof ControlFailure && invalidWrite.uncertain && invalidWrite.operationId === 'malformed-operation',
    'a malformed write response retains its uncertain operation without replay', checks);

  let shapeCalls = 0;
  const wrongShape = new ControlClient('http://control.test', async () => {
    shapeCalls++;
    return jsonResponse([]);
  });
  let shapeFailure: unknown;
  try { await wrongShape.write('/control/v1/automation/change', {}, objectValue, 'shape-operation'); }
  catch (error) { shapeFailure = error; }
  assert(shapeFailure instanceof ControlFailure && shapeFailure.uncertain && shapeFailure.operationId === 'shape-operation'
    && shapeFailure.code === 'control_response_invalid' && shapeCalls === 1 && wrongShape.pendingWrites === 0,
    'valid JSON with an invalid write shape preserves the original unknown operation without replay', checks);
  let shapeRead: unknown;
  try { await wrongShape.read('/control/v1/automation', objectValue); }
  catch (error) { shapeRead = error; }
  assert(shapeRead instanceof TypeError && !(shapeRead instanceof ControlFailure),
    'a read shape failure remains a protocol error without an unknown write identity', checks);

  let readCalls = 0;
  let startedRead!: () => void;
  const readStarted = new Promise<void>(resolve => { startedRead = resolve; });
  const readClient = new ControlClient('http://control.test', async (_input, init) => {
    readCalls++;
    startedRead();
    return await new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new DOMException('cancelled', 'AbortError')), { once: true });
    });
  });
  const readOwner = new AbortController();
  const read = readClient.read('/control/v1/status', value => value, readOwner.signal);
  await readStarted;
  readOwner.abort();
  let readFailure: unknown;
  try { await read; } catch (error) { readFailure = error; }
  assert(readFailure instanceof DOMException && readFailure.name === 'AbortError' && readCalls === 1,
    'owner cancellation aborts an in-flight read without retrying it', checks);

  let httpCalls = 0;
  const conflict = new ControlClient('http://control.test', async () => {
    httpCalls++;
    return jsonResponse({ code: 'revision_conflict', correlationId: 'correlation-a' }, 409);
  });
  let httpFailure: unknown;
  try { await conflict.read('/control/v1/settings', value => value); } catch (error) { httpFailure = error; }
  assert(httpFailure instanceof ControlFailure && httpFailure.status === 409
    && httpFailure.code === 'revision_conflict' && httpFailure.correlationId === 'correlation-a'
    && httpCalls === 1,
  'HTTP failures retain the stable code and correlation identity', checks);

  const browserClient = new ControlClient();
  const browserResponse = await browserClient.read('/control/v1/status', objectValue);
  assert(browserResponse.fixture === 'native-window-fetch',
    'the default ControlClient fetch works with the real browser window receiver', checks);

  if (document.hidden) throw new Error('Native control fixture document must be visible');
  const encoder = new TextEncoder();
  let snapshots = 0;
  let streams = 0;
  let retrySignal: AbortSignal | undefined;
  let retryController: ReadableStreamDefaultController<Uint8Array> | undefined;
  const firstEvent = eventFrame({ streamId: 'stream-a', instanceId: 'instance-a', sequence: 41, data: { marker: 'event' } });
  const missingEvent = eventFrame({ streamId: 'stream-a', instanceId: 'instance-a', sequence: 43, data: { marker: 'gap' } });
  const splitAt = firstEvent.indexOf('"sequence"') + 7;
  const firstChunks = [firstEvent.slice(0, splitAt), firstEvent.slice(splitAt) + missingEvent];
  const eventClient = new ControlClient('http://control.test', async (input, init) => {
    const url = new URL(String(input));
    if (url.pathname === '/control/v1/events/snapshot') {
      snapshots++;
      return jsonResponse({ instanceId:'instance-a', streamId: 'stream-a', sequence: snapshots === 1 ? 40 : 42,
        state: { instanceId: 'instance-a', marker: snapshots === 1 ? 'initial' : 'authoritative-after-gap' } });
    }
    if (url.pathname === '/control/v1/events') {
      streams++;
      if (streams === 1) {
        let index = 0;
        return new Response(new ReadableStream<Uint8Array>({
          pull(controller) {
            const chunk = firstChunks[index++];
            if (chunk === undefined) controller.close();
            else controller.enqueue(encoder.encode(chunk));
          },
        }), { headers: { 'Content-Type': 'text/event-stream' } });
      }
      retrySignal = init?.signal ?? undefined;
      return new Response(new ReadableStream<Uint8Array>({
        start(controller) { retryController = controller; },
      }), { headers: { 'Content-Type': 'text/event-stream' } });
    }
    throw new Error('Unexpected control request: ' + url.pathname);
  });
  const reported: unknown[] = [];
  let reportGap!: () => void;
  const gapReported = new Promise<void>(resolve => { reportGap = resolve; });
  const feed = new EventFeed(eventClient, error => {
    reported.push(error);
    if (error instanceof Error && error.message === 'event_gap') reportGap();
  });
  const observed: EventSnapshot[] = [];
  const unsubscribe = feed.subscribe(snapshot => observed.push(snapshot));
  await gapReported;
  await waitFor(() => snapshots === 2 && streams === 2, 'authoritative snapshot after an SSE gap');
  assert(observed.map(snapshot => snapshot.sequence).join(',') === '40,41,42'
    && observed[2]?.state.marker === 'authoritative-after-gap'
    && reported.some(error => error instanceof Error && error.message === 'event_gap'),
  'split SSE frames publish in order and a missing sequence replaces state from a fresh snapshot', checks);

  unsubscribe();
  assert(retrySignal?.aborted === true, 'unsubscribing the last owner aborts the active SSE request', checks);
  retryController?.close();
  await new Promise(resolve => setTimeout(resolve, 20));
  assert(observed.length === 3 && snapshots === 2,
    'an unloaded owner receives no late events and does not reconnect', checks);

  let foreignSnapshots = 0;
  let foreignStreams = 0;
  let foreignRetrySignal: AbortSignal | undefined;
  let foreignRetryController: ReadableStreamDefaultController<Uint8Array> | undefined;
  const foreignClient = new ControlClient('http://control.test', async (input, init) => {
    const url = new URL(String(input));
    if (url.pathname === '/control/v1/events/snapshot') {
      foreignSnapshots++;
      return jsonResponse({ instanceId: 'instance-a', streamId: 'stream-foreign-check', sequence: 10,
        state: { marker: foreignSnapshots === 1 ? 'before-foreign-event' : 'authoritative-after-instance-gap' } });
    }
    if (url.pathname === '/control/v1/events') {
      foreignStreams++;
      if (foreignStreams === 1) return new Response(new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(encoder.encode(eventFrame({ streamId: 'stream-foreign-check', instanceId: 'instance-b',
            sequence: 11, data: { marker: 'foreign-instance' } })));
          controller.close();
        },
      }), { headers: { 'Content-Type': 'text/event-stream' } });
      foreignRetrySignal = init?.signal ?? undefined;
      return new Response(new ReadableStream<Uint8Array>({
        start(controller) { foreignRetryController = controller; },
      }), { headers: { 'Content-Type': 'text/event-stream' } });
    }
    throw new Error('Unexpected control request: ' + url.pathname);
  });
  const foreignReported: unknown[] = [];
  let reportForeignGap!: () => void;
  const foreignGapReported = new Promise<void>(resolve => { reportForeignGap = resolve; });
  const foreignFeed = new EventFeed(foreignClient, error => {
    foreignReported.push(error);
    if (error instanceof Error && error.message === 'event_gap') reportForeignGap();
  });
  const foreignObserved: EventSnapshot[] = [];
  const unsubscribeForeign = foreignFeed.subscribe(snapshot => foreignObserved.push(snapshot));
  await foreignGapReported;
  await waitFor(() => foreignSnapshots === 2 && foreignStreams === 2, 'a fresh snapshot after a wrong-instance event');
  const latestForeignSnapshot = foreignObserved[foreignObserved.length - 1];
  assert(foreignObserved.every(snapshot => snapshot.state.marker !== 'foreign-instance')
    && latestForeignSnapshot?.instanceId === 'instance-a'
    && latestForeignSnapshot.state.marker === 'authoritative-after-instance-gap'
    && foreignReported.some(error => error instanceof Error && error.message === 'event_gap'),
  'an event from a different instance is rejected and the feed reloads authoritative state', checks);
  unsubscribeForeign();
  assert(foreignRetrySignal?.aborted === true, 'the resynchronized feed aborts its stream after its last owner leaves', checks);
  foreignRetryController?.close();
  await new Promise(resolve => setTimeout(resolve, 20));

  await mediaPoolContract(checks);
  checks.push(...(await executionCommandContract()).checks);
  checks.push(...(await liveLogContract()).checks);
  checks.push(...(await completionCommands()).checks);
  checks.push(...adapterResults().checks);

  return { checks, checkCount: checks.length };
}
