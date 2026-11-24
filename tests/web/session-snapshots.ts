import assert from 'node:assert/strict';
import { ControlClient } from '../../apps/web/src/control/client';
import { AppSession } from '../../apps/web/src/app/app-session';
import { createHostState } from '../../apps/web/src/app/host-state';
import { HostSnapshots } from '../../apps/web/src/app/host-snapshots';

const settle = async () => { for (let index = 0; index < 40; index++) await Promise.resolve(); };

export async function snapshotCases(): Promise<string[]> {
  const cases: string[] = [];
  for (const change of ['none', 'revision', 'stream', 'reconnect', 'authority', 'revocation', 'navigation-reconnect'] as const) {
    const state = createHostState(), client = new ControlClient('http://127.0.0.1');
    let access = { instanceId: 'instance', hostVersion: '0.18.0', capabilities: ['status.read', 'operations.read'] };
    let snapshot = { instanceId: 'instance', streamId: 'stream', sequence: 1,
      state: { catalogRevision: 1, settingsRevision: 1, interfaceRevision: 1, mutationSequence: 1 } };
    const reads = new Map<string, number>();
    let pendingOperations = 0;
    const staleStatus = Promise.withResolvers<void>(), statusRequested = Promise.withResolvers<void>();
    let holdStatus = false;
    let stream!: ReadableStreamDefaultController<Uint8Array>;
    let opened = 0;
    client.read = (async (path: string) => {
      const count = (reads.get(path) ?? 0) + 1; reads.set(path, count);
      if (path === '/control/v1/capabilities') return access;
      if (path === '/control/v1/events/snapshot') {
        if (count === 2 && change === 'revision') snapshot = { ...snapshot, sequence: 2,
          state: { ...snapshot.state, catalogRevision: 2, mutationSequence: 2 } };
        if (count === 2 && change === 'stream') snapshot = { ...snapshot, streamId: 'replacement' };
        return structuredClone(snapshot);
      }
      if (path === '/control/v1/automation') return { catalogRevision: snapshot.state.catalogRevision };
      if (path === '/control/v1/settings') return { saved: { revision: snapshot.state.settingsRevision } };
      if (path === '/control/v1/settings/interface') return { revision: snapshot.state.interfaceRevision };
      if (path === '/control/v1/status') {
        const value = { instanceId: access.instanceId, pendingOperations };
        if (holdStatus) { holdStatus = false; statusRequested.resolve(); await staleStatus.promise; }
        return value;
      }
      throw Error('unexpected_session_read:' + path);
    }) as never;
    client.response = async (_path, signal) => new Response(new ReadableStream<Uint8Array>({ start(controller) {
      opened++;
      stream = controller;
      signal!.addEventListener('abort', () => controller.close(), { once: true });
    } }));
    const snapshots = new HostSnapshots(client, state, { observe(value: typeof state.interface) {
      state.interface = value;
    } } as never);
    const failures: unknown[] = [];
    const session = new AppSession(client, state, snapshots, error => failures.push(error), () => 'users');
    try {
      await session.start(); await settle();
      assert.equal(state.settings?.saved.revision, snapshot.state.settingsRevision);
      assert.equal(state.interface?.revision, snapshot.state.interfaceRevision);
      assert.equal(state.status?.pendingOperations, 0);
      assert.equal(snapshots.streamId, snapshot.streamId);
      assert.equal(state.automation?.catalogRevision, snapshot.state.catalogRevision);
      assert.equal(snapshots.mutation, snapshot.state.mutationSequence);
      if (change === 'reconnect') {
        Object.assign(document, { hidden: true }); document.dispatchEvent(new Event('visibilitychange')); await settle();
        pendingOperations = 3;
        snapshot = { ...snapshot, sequence: snapshot.sequence + 1,
          state: { ...snapshot.state, settingsRevision: 3, mutationSequence: 3 } };
        Object.assign(document, { hidden: false }); document.dispatchEvent(new Event('visibilitychange')); await settle();
        assert.equal(state.status?.pendingOperations, 3);
        assert.equal(state.settings?.saved.revision, 3);
        assert.equal(snapshots.mutation, 3);
      }
      if (change === 'authority' || change === 'revocation') {
        if (change === 'authority') client.authenticate('replacement-fixture-credential');
        else access = { ...access, capabilities: ['status.read'] };
        pendingOperations = 4;
        await session.start(); await settle();
        assert.equal(opened, 2);
        assert.equal(state.status?.pendingOperations, 4);
        assert.deepEqual(state.capabilities?.capabilities, access.capabilities);
      }
      if (change === 'navigation-reconnect') {
        holdStatus = true;
        const navigation = session.navigate('dashboard', true);
        await statusRequested.promise;
        Object.assign(document, { hidden: true }); document.dispatchEvent(new Event('visibilitychange'));
        await settle();
        pendingOperations = 6;
        snapshot = { ...snapshot, streamId: 'after-reconnect', sequence: 1,
          state: { ...snapshot.state, mutationSequence: 2 } };
        Object.assign(document, { hidden: false }); document.dispatchEvent(new Event('visibilitychange'));
        await navigation;
        staleStatus.resolve();
        await settle();
        assert.equal(state.page, 'dashboard');
        assert.equal(state.status?.pendingOperations, 6);
        assert.equal(snapshots.streamId, 'after-reconnect');
      }
      snapshot = { ...snapshot, sequence: snapshot.sequence + 1, state: { ...snapshot.state, settingsRevision: 4 } };
      stream.enqueue(new TextEncoder().encode('data: ' + JSON.stringify({ format: 'nexus.event', version: 1,
        kind: 'state', topic: 'host.state', streamId: snapshot.streamId, instanceId: snapshot.instanceId,
        sequence: snapshot.sequence, subject: { type: 'instance', id: snapshot.instanceId }, data: snapshot.state }) + '\n\n'));
      await settle();
      assert.equal(state.settings?.saved.revision, 4);
      assert.equal(state.revision, snapshot.sequence);
      assert.deepEqual(failures, []);
      cases.push('initial subscription preserves current revisions and reconnect refreshes visible state: ' + change);
    } finally { staleStatus.resolve(); session.dispose(); await settle(); }
  }
  return [...cases, ...await overlappingReads()];
}

async function overlappingReads(): Promise<string[]> {
  const state = createHostState(), client = new ControlClient('http://127.0.0.1');
  const calls: { signal: AbortSignal; finish: (catalogRevision: number) => void }[] = [];
  client.read = (async (path: string, _decode: unknown, signal: AbortSignal) => {
    assert.equal(path, '/control/v1/automation');
    const gate = Promise.withResolvers<{ catalogRevision: number }>();
    calls.push({ signal, finish: catalogRevision => gate.resolve({ catalogRevision }) });
    return gate.promise;
  }) as never;
  const snapshots = new HostSnapshots(client, state, {} as never);
  const plan = { automation: true, settings: false, status: false, interface: false };
  const target = (catalog: number) => ({ catalog, settings: 1, appearance: 1, mutation: catalog });
  const navigation = new AbortController(), feed = new AbortController();
  const first = snapshots.read(plan, navigation.signal, target(2));
  const second = snapshots.read(plan, feed.signal, target(2));
  assert.equal(calls.length, 1, 'same revision issues one transport GET for two active owners');
  feed.abort();
  await assert.rejects(second, { name: 'AbortError' });
  assert.equal(calls[0]!.signal.aborted, false);
  calls[0]!.finish(2);
  await first;
  assert.equal(state.automation?.catalogRevision, 2);

  const cancelled = new AbortController();
  const abandoned = snapshots.read(plan, cancelled.signal, target(3));
  cancelled.abort();
  calls[1]!.finish(3);
  await assert.rejects(abandoned, { name: 'AbortError' });
  assert(calls[1]!.signal.aborted);
  assert.equal(state.automation?.catalogRevision, 2);
  const older = snapshots.read(plan, undefined, target(3));
  const newer = snapshots.read(plan, undefined, target(4));
  assert.equal(calls.length, 4);
  calls[3]!.finish(4);
  await newer;
  calls[2]!.finish(3);
  await older;
  assert.equal(state.automation?.catalogRevision, 4);

  const previousToken = snapshots.read(plan, undefined, target(5));
  client.authenticate('test-new-credential');
  await assert.rejects(previousToken, { name: 'AbortError' });
  const currentToken = snapshots.read(plan, undefined, target(5));
  calls[5]!.finish(5);
  await currentToken;
  calls[4]!.finish(5);
  await settle();
  assert(calls[4]!.signal.aborted);
  assert.equal(state.automation?.catalogRevision, 5);

  const previousStream = snapshots.read(plan, undefined, target(6));
  snapshots.reset();
  await assert.rejects(previousStream, { name: 'AbortError' });
  calls[6]!.finish(6);
  await settle();
  assert.equal(state.automation, null);
  const resumed = snapshots.read(plan, undefined, target(1));
  calls[7]!.finish(1);
  await resumed;
  assert.equal(state.automation?.catalogRevision, 1);
  return ['same revision folds two reads into one GET and cancelling one owner preserves its peer',
    'cancelling the last owner revokes a late response and a newer revision starts a separate GET',
    'credential replacement revokes a pending read even at the same target revision',
    'reset aborts old requests and permits the replacement instance to start from a lower revision'];
}
