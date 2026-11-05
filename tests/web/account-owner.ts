import assert from 'node:assert/strict';
import { createRenderer, defineComponent, h, nextTick, ref } from 'vue';
import { useAccountSummaries } from '../../apps/web/src/pages/account-summary';
import { fixture } from './automation-fixture';

export async function ownerCases(): Promise<string[]> {
  const previousDocument = Object.getOwnPropertyDescriptor(globalThis, 'document');
  Object.defineProperty(globalThis, 'document', { configurable: true, value: Object.assign(new EventTarget(), { hidden: false }) });
  const previousSet = globalThis.setInterval, previousClear = globalThis.clearInterval, previousNow = Date.now;
  const timers = new Map<number, () => void>(); let sequence = 0, now = Date.parse('2026-11-01T00:00:00Z');
  globalThis.setInterval = ((callback: () => void) => { timers.set(++sequence, callback); return sequence; }) as never;
  globalThis.clearInterval = ((id: number) => { timers.delete(id); }) as never;
  Date.now = () => now;
  const renderer = createRenderer<any, any>({ patchProp() {}, insert(node, parent) { node.parent = parent; }, remove() {},
    createElement: () => ({}), createText: () => ({}), createComment: () => ({}), setText() {}, setElementText() {},
    parentNode: node => node.parent, nextSibling: () => null });
  const cases: string[] = [];
  async function settle() { await nextTick(); await Promise.resolve(); await Promise.resolve(); await nextTick(); }
  try {
    for (const scenario of ['shared', 'due', 'network', 'hidden', 'legacy-status', 'automation.read', 'control.status.read', 'records.read', 'scheduling.read']) {
      const item = fixture(), visible = ref(true), users = Array.from({ length: scenario === 'shared' ? 100 : 1 }, () => crypto.randomUUID());
      item.store.state.automation!.bundle.users = users.map(id => ({ id, name: id, order: 0 })) as never;
      const capabilities = ['automation.read', 'control.status.read', 'records.read', 'scheduling.read']
        .filter(value => value !== scenario && !(scenario === 'legacy-status' && value === 'control.status.read'));
      if (scenario === 'legacy-status') capabilities.push('status.read');
      item.store.state.capabilities = { ...item.store.state.capabilities, capabilities } as never;
      let requests = 0, signal: AbortSignal | undefined, release: (() => void) | undefined, blocked = false;
      item.store.client.read = (async (_path: string, decode: (value: unknown) => unknown, cancellation: AbortSignal) => {
        requests++; signal = cancellation;
        if (blocked) await new Promise<void>(resolve => { release = resolve; });
        if (scenario === 'network') throw new Error('controlled_network_failure');
        return decode({ catalogRevision: item.store.state.automation!.catalogRevision, progressRevision: 1,
          observedAt: new Date(now).toISOString(), items: users.map(userId => ({ userId, summaryReason: 'latest',
            summaryTone: requests === 1 ? 'bad' : 'ok', runId: '20000000-0000-4000-8000-000000000001', activeCount: 1,
            nextRunAt: new Date(Date.parse('2026-11-01T00:00:00Z') + 1000).toISOString(), queueId: item.queue.id })) });
      }) as never;
      let state: ReturnType<typeof useAccountSummaries>;
      const app = renderer.createApp(defineComponent({ setup() {
        state = useAccountSummaries(item.store, () => visible.value); return () => h('div');
      } }));
      try {
        app.mount({}); await settle();
        if (scenario.includes('.') || scenario === 'legacy-status') {
          assert.equal(requests, 0); assert.equal(timers.size, 0); assert(state!.error);
        } else if (scenario === 'network') {
          assert.equal(requests, 1); assert.match(state!.error, /controlled_network_failure/);
        } else {
          assert.equal(requests, 1); assert.equal(timers.size, 1); assert.equal(state!.items.size, users.length);
          if (scenario === 'due') {
            now = Date.parse('2026-11-01T00:00:00Z') + 1001;
            for (const timer of timers.values()) timer(); await settle();
            assert.equal(requests, 2); assert.equal(state!.items.get(users[0]!)!.summaryTone, 'ok');
            for (const timer of timers.values()) timer(); await settle(); assert.equal(requests, 2);
          }
          if (scenario === 'hidden') {
            blocked = true; item.store.state.automation = { ...item.store.state.automation!,
              catalogRevision: item.store.state.automation!.catalogRevision + 1 }; await settle();
            const previous = state!.items;
            visible.value = false; await settle(); assert(signal!.aborted); assert.equal(timers.size, 0);
            release!(); await settle(); assert.equal(state!.items, previous);
          }
        }
        cases.push('actual account owner request/clock/error/permission lifecycle: ' + scenario);
      } finally { app.unmount(); item.store.dispose(); assert.equal(timers.size, 0); now = Date.parse('2026-11-01T00:00:00Z'); }
    }
    return cases;
  } finally {
    globalThis.setInterval = previousSet; globalThis.clearInterval = previousClear; Date.now = previousNow;
    if (previousDocument) Object.defineProperty(globalThis, 'document', previousDocument); else Reflect.deleteProperty(globalThis, 'document');
  }
}
