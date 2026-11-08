import assert from 'node:assert/strict';
import { ControlClient, ControlFailure } from '../../apps/web/src/control/client';
import { abortable } from '../../apps/web/src/control/abortable';
import { AppSession } from '../../apps/web/src/app/app-session';
import { createHostState } from '../../apps/web/src/app/host-state';
import { HostSnapshots } from '../../apps/web/src/app/host-snapshots';

function deferred() {
  let resolve!: () => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<void>((accept, fail) => { resolve = accept; reject = fail; });
  return { promise, resolve, reject };
}
const settle = async () => { for (let index = 0; index < 30; index++) await Promise.resolve(); };

export async function sessionCases(): Promise<string[]> {
  const state = createHostState(), client = new ControlClient('http://127.0.0.1');
  const snapshot = { instanceId: 'instance', streamId: 'stream', sequence: 1,
    state: { catalogRevision: 1, settingsRevision: 1, interfaceRevision: 1, mutationSequence: 1, executionRevision: 0 } };
  const access = { instanceId: 'instance', hostVersion: '0.18.0', capabilities: ['status.read'] };
  client.read = (async (route: string, decode: (value: unknown) => unknown) => {
    if (route === '/control/v1/capabilities') return decode(access);
    if (route === '/control/v1/events/snapshot') return decode(snapshot);
    throw Error('unexpected_session_read:' + route);
  }) as never;
  let opened = 0, stopped = 0, stream!: ReadableStreamDefaultController<Uint8Array>;
  client.response = async (_route, signal) => {
    opened++;
    return new Response(new ReadableStream<Uint8Array>({ start(controller) {
      stream = controller;
      signal!.addEventListener('abort', () => { stopped++; controller.close(); }, { once: true });
    } }), { headers: { 'Content-Type': 'text/event-stream' } });
  };
  const snapshots = new HostSnapshots(client, state, {} as never);
  const entered = deferred(), replacement = deferred(), release = deferred();
  let reads = 0;
  snapshots.read = async (_plan, signal) => {
    if (++reads === 1) {
      const pending = deferred();
      signal!.addEventListener('abort', () => pending.reject(signal!.reason), { once: true });
      entered.resolve(); await pending.promise;
    } else if (reads === 2) { replacement.resolve(); await release.promise; }
    state.automation = { catalogRevision: 1 } as never;
    state.settings = { saved: { revision: 1 } } as never;
    state.interface = { revision: 1 } as never;
    state.status = {} as never;
    return { settings: 0, status: 0 };
  };
  snapshots.aligned = () => true;
  const failures: unknown[] = [];
  const session = new AppSession(client, state, snapshots, reason => failures.push(reason), () => 'dashboard');
  try {
    const initial = assert.rejects(session.start(), { name: 'AbortError' });
    await entered.promise;
    const navigation = session.navigate('dispatch', true);
    await initial; await replacement.promise;
    assert.equal(opened, 0);
    release.resolve(); await navigation; await settle();
    assert.equal(state.page, 'dispatch'); assert.equal(opened, 1);
    stream.enqueue(new TextEncoder().encode('data: ' + JSON.stringify({ format: 'nexus.event', version: 1,
      kind: 'state', topic: 'host.state', streamId: 'stream', instanceId: 'instance', sequence: 2,
      subject: { type: 'instance', id: 'instance' }, data: { ...snapshot.state, executionRevision: 1 } }) + '\n\n'));
    await settle(); assert.equal(state.revision, 2); assert.equal(state.events?.executionRevision, 1);
    await session.navigate('users', true); await settle(); assert.equal(opened, 1);
    assert.deepEqual(failures, []);
  } finally { release.resolve(); session.dispose(); await settle(); }
  assert.equal(stopped, 1);
  return ['a replacement startup navigation establishes the actual event feed and receives later execution changes',
    'later navigation reuses one event stream and disposal aborts its owner', ...await startupPairCases(), ...await startupStreamCase()];
}

async function startupPairCases(): Promise<string[]> {
  const cases: string[] = [];
  for (const mode of ['success', 'cancel', 'access-denied', 'snapshot-denied', 'instance-mismatch'] as const) {
    const state = createHostState(), client = new ControlClient('http://127.0.0.1');
    const access = { instanceId: 'instance', hostVersion: '0.18.0', capabilities: ['status.read'] };
    const snapshot = { instanceId: mode === 'instance-mismatch' ? 'other' : 'instance', streamId: 'stream', sequence: 1,
      state: { catalogRevision: 1, settingsRevision: 1, interfaceRevision: 1, mutationSequence: 1 } };
    const old = { instanceId: 'previous', hostVersion: '0.18.0', capabilities: ['status.read'] };
    if (mode === 'instance-mismatch') { state.capabilities = old; state.events = { retained: true }; }
    const accessGate = deferred(), snapshotGate = deferred(), paths: string[] = [], signals: AbortSignal[] = [];
    client.read = (async (path: string, decode: (value: unknown) => unknown, signal: AbortSignal) => {
      paths.push(path); signals.push(signal);
      if (path === '/control/v1/capabilities') {
        await abortable(accessGate.promise, signal);
        if (mode === 'access-denied') throw new ControlFailure(403, 'permission_denied');
        return decode(access);
      }
      if (path === '/control/v1/events/snapshot') {
        await abortable(snapshotGate.promise, signal);
        if (mode === 'snapshot-denied') throw new ControlFailure(403, 'permission_denied');
        return decode(snapshot);
      }
      throw Error('unexpected_startup_read:' + path);
    }) as never;
    const snapshots = new HostSnapshots(client, state, {} as never);
    let synchronized = 0;
    snapshots.read = async () => { synchronized++; return { settings: 0, status: 0 }; };
    snapshots.aligned = () => true;
    const session = new AppSession(client, state, snapshots, () => {}, () => 'dashboard');
    const owner = new AbortController();
    try {
      const pending = session.start(owner.signal);
      assert.deepEqual(new Set(paths), new Set(['/control/v1/events/snapshot', '/control/v1/capabilities']));
      const failed = mode === 'success' ? undefined : assert.rejects(pending, error =>
        mode === 'cancel' ? error instanceof DOMException && error.name === 'AbortError' :
          error instanceof ControlFailure && error.code === (mode === 'instance-mismatch' ? 'control_instance_changed' : 'permission_denied'));
      if (mode === 'cancel') owner.abort();
      else if (mode === 'access-denied') accessGate.resolve();
      else if (mode === 'snapshot-denied') snapshotGate.resolve();
      else { accessGate.resolve(); snapshotGate.resolve(); }
      if (failed) {
        await failed;
        assert.equal(synchronized, 0);
        assert(signals.every(signal => signal.aborted));
        if (mode === 'instance-mismatch') {
          assert.equal(state.capabilities, old);
          assert.equal(state.events?.retained, true);
        } else assert.equal(state.capabilities, null);
      } else {
        await pending;
        assert.equal(synchronized, 1);
        assert.equal(state.capabilities?.instanceId, access.instanceId);
        assert.equal(paths.length, 2);
      }
      cases.push('startup reads access and event state concurrently with cancellation and identity protection: ' + mode);
    } finally { owner.abort(); accessGate.resolve(); snapshotGate.resolve(); session.dispose(); }
  }
  return cases;
}

async function startupStreamCase(): Promise<string[]> {
  const state = createHostState(), client = new ControlClient('http://127.0.0.1');
  const access = { instanceId: 'instance', hostVersion: '0.18.0', capabilities: ['status.read'] };
  const first = { instanceId: 'instance', streamId: 'first', sequence: 1,
    state: { catalogRevision: 1, settingsRevision: 1, interfaceRevision: 1, mutationSequence: 1 } };
  const next = { ...first, streamId: 'next', sequence: 2 };
  const snapshots = new HostSnapshots(client, state, {} as never);
  snapshots.observe(first);
  snapshots.read = async () => ({ settings: 0, status: 0 });
  snapshots.aligned = () => true;
  const gate = deferred(), counts = new Map<string, number>();
  client.read = (async (path: string, decode: (value: unknown) => unknown, signal: AbortSignal) => {
    const count = (counts.get(path) ?? 0) + 1; counts.set(path, count);
    if (count === 1) await abortable(gate.promise, signal);
    if (path === '/control/v1/capabilities') return decode(access);
    if (path === '/control/v1/events/snapshot') return decode(count === 1 ? first : next);
    throw Error('unexpected_stream_read:' + path);
  }) as never;
  const session = new AppSession(client, state, snapshots, () => {}, () => 'dashboard');
  try {
    const pending = session.start();
    snapshots.observe(next);
    gate.resolve(); await pending;
    assert.equal(counts.get('/control/v1/capabilities'), 2);
    assert.equal(counts.get('/control/v1/events/snapshot'), 2);
    assert.equal(snapshots.streamId, 'next');
    return ['a stream replacement during initial concurrent reads retries the aligned pair'];
  } finally { gate.resolve(); session.dispose(); }
}
