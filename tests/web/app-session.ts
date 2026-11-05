import assert from 'node:assert/strict';
import { ControlClient } from '../../apps/web/src/control/client';
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
    const initial = session.start();
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
    'later navigation reuses one event stream and disposal aborts its owner'];
}
