import { createApp, h, nextTick, ref } from 'vue';
import CompletionNotice from '../../apps/web/src/pages/CompletionNotice.vue';
import { ControlClient } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';

export async function completionRefresh(check: (value: unknown, name: string) => void): Promise<void> {
  const originalInterval = globalThis.setInterval, originalClear = globalThis.clearInterval, originalNow = Date.now;
  const timers = new Map<number, { delay: number; run: () => void }>();
  let identifier = 70000, requests = 0, pending = false, fail = false, time = originalNow();
  Date.now = () => time;
  globalThis.setInterval = ((run: () => void, delay: number) => {
    const id = ++identifier; timers.set(id, { delay, run }); return id;
  }) as typeof setInterval;
  globalThis.clearInterval = ((id: number) => {
    if (!timers.delete(id)) originalClear(id);
  }) as typeof clearInterval;
  const client = new ControlClient(location.origin, async input => {
    check(new URL(String(input)).pathname === '/control/v1/completion/pending', 'completion refresh uses the current readonly endpoint');
    requests++;
    if (fail) return Response.json({ code: 'unavailable' }, { status: 503 });
    return Response.json({ operationId: pending ? 'completion-a' : null, action: pending ? 'Exit' : null,
      phase: pending ? 'Countdown' : null, notBeforeUtc: pending ? new Date(time + 3000).toISOString() : null,
      serverTimeUtc: new Date(time).toISOString(), canCancel: pending, failureCode: null });
  });
  const store = createStore(client), visible = ref(true);
  store.state.capabilities = { instanceId: 'instance-a', hostVersion: '0.18.0', capabilities: ['control.status.read'] };
  store.state.events = { mutationSequence: 0, pendingOperations: 0, completedRuns: 0, recoveryRevision: 0 };
  const root = document.createElement('div'); document.body.append(root);
  const app = createApp({ render: () => h(CompletionNotice, { store, visible: visible.value }) });
  const settle = async () => { await nextTick(); await new Promise(resolve => setTimeout(resolve, 0)); await nextTick(); };
  try {
    app.mount(root); await settle();
    check(requests === 1 && timers.size === 0 && !root.querySelector('section'),
      'an empty completion surface has no countdown clock or recurring status polling');
    pending = true; time += 5000;
    store.state.events = { ...store.state.events, mutationSequence: 1 }; await settle();
    check(requests === 2 && root.querySelector('section') && timers.size === 2,
      'a committed state event discovers the countdown even when aggregate operation counts stay equal');
    check(root.textContent?.includes('3'), 'the current countdown paints its server-relative remaining seconds');
    time += 1100; timers.forEach(timer => { if (timer.delay === 1000) timer.run(); }); await settle();
    check(root.textContent?.includes('2'), 'the visible countdown clock advances without a network request');
    pending = false; time += 5000;
    store.state.events = { ...store.state.events, mutationSequence: 2 }; await settle();
    check(requests === 3 && timers.size === 0 && !root.querySelector('section'),
      'settlement clears both live timers and removes the completed surface');
    fail = true; time += 5000;
    store.state.events = { ...store.state.events, mutationSequence: 3 }; await settle();
    check(requests === 4 && timers.size === 1 && [...timers.values()][0]?.delay === 5000,
      'a failed status read retains bounded readonly recovery without an empty countdown clock');
    fail = false; time += 5000; timers.forEach(timer => timer.run()); await settle();
    check(requests === 5 && timers.size === 0, 'a successful empty recovery releases its retry polling');
    visible.value = false; await settle();
    pending = true; store.state.events = { ...store.state.events, mutationSequence: 4 }; await settle();
    check(requests === 5 && timers.size === 0, 'hidden completion consumers perform no display polling or clock work');
    visible.value = true; await settle();
    check(requests === 6 && timers.size === 2, 'showing the surface reads current state and resumes an actual countdown');
    app.unmount(); await settle();
    check(timers.size === 0, 'unmount releases every completion display timer');
  } finally {
    if (root.hasChildNodes()) app.unmount();
    root.remove(); store.dispose();
    Date.now = originalNow; globalThis.setInterval = originalInterval; globalThis.clearInterval = originalClear;
  }
}
