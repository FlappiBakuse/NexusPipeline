import assert from 'node:assert/strict';
import { ControlClient } from '../../apps/web/src/control/client';
import { AppSession } from '../../apps/web/src/app/app-session';
import { createHostState } from '../../apps/web/src/app/host-state';
import { HostSnapshots } from '../../apps/web/src/app/host-snapshots';

const settle = async () => { for (let index = 0; index < 40; index++) await Promise.resolve(); };

export async function snapshotCases(): Promise<string[]> {
  const cases: string[] = [];
  for (const change of ['none', 'revision', 'stream', 'reconnect'] as const) {
    const state = createHostState(), client = new ControlClient('http://127.0.0.1');
    const access = { instanceId: 'instance', hostVersion: '0.18.0', capabilities: ['status.read'] };
    let snapshot = { instanceId: 'instance', streamId: 'stream', sequence: 1,
      state: { catalogRevision: 1, settingsRevision: 1, interfaceRevision: 1, mutationSequence: 1 } };
    const reads = new Map<string, number>();
    let pendingOperations = 0;
    let stream!: ReadableStreamDefaultController<Uint8Array>;
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
      if (path === '/control/v1/status') return { instanceId: access.instanceId, pendingOperations };
      throw Error('unexpected_session_read:' + path);
    }) as never;
    client.response = async (_path, signal) => new Response(new ReadableStream<Uint8Array>({ start(controller) {
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
      snapshot = { ...snapshot, sequence: snapshot.sequence + 1, state: { ...snapshot.state, settingsRevision: 4 } };
      stream.enqueue(new TextEncoder().encode('data: ' + JSON.stringify({ format: 'nexus.event', version: 1,
        kind: 'state', topic: 'host.state', streamId: snapshot.streamId, instanceId: snapshot.instanceId,
        sequence: snapshot.sequence, subject: { type: 'instance', id: snapshot.instanceId }, data: snapshot.state }) + '\n\n'));
      await settle();
      assert.equal(state.settings?.saved.revision, 4);
      assert.equal(state.revision, snapshot.sequence);
      assert.deepEqual(failures, []);
      cases.push('initial subscription preserves current revisions and reconnect refreshes visible state: ' + change);
    } finally { session.dispose(); await settle(); }
  }
  return cases;
}
