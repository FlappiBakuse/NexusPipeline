import { createApp, h, nextTick, ref } from 'vue';
import AttemptDetail from '../../apps/web/src/pages/AttemptDetail.vue';
import { ControlClient } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';
import type { AttemptFacts } from '../../apps/web/src/control/records';

export async function historyIndexContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const id = '10000000-0000-4000-8000-000000000001';
  let pending = true, reads = 0, reports = 0, images = 0, logs = 0;
  const client = new ControlClient(location.origin, async input => {
    reads++;
    if (String(input).endsWith('/report')) { reports++; return Response.json({ operationId: id, runId: id, attemptId: id,
      ordinal: 1, phase: 'committed', canCancel: false, cancellationRequested: false, steps: [] }); }
    if (String(input).includes('/images?')) { images++; return Response.json({ items: [], nextPosition: null }); }
    logs++;
    return pending ? Response.json({ code: 'history_index_pending' }, { status: 503 }) : Response.json({ items: [], nextPosition: null });
  });
  const owner = createStore(client), store = { ...owner, can: () => true };
  const visible = ref(true);
  const mount = document.createElement('section'); fixture.prepend(mount); mount.scrollIntoView();
  const facts: AttemptFacts = { runId: id, attemptId: id, ordinal: 1, engine: 'succeeded', business: 'succeeded',
    lifecycle: 'completed', recovery: 'restored', reasonCode: 'fixture' };
  const app = createApp({ render: () => h(AttemptDetail, { store, runId: id, attemptId: id, visible: visible.value, facts }) });
  app.mount(mount);
  const wait = async (condition: () => boolean) => {
    const until = Date.now() + 5000;
    while (!condition() && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 20));
    check(condition(), 'history index state reaches its visible result');
  };
  try {
    await wait(() => !!mount.querySelector('[role="status"]'));
    check(mount.textContent?.includes('历史索引正在补齐') && !mount.querySelector('.history-log-toggle'),
      'pending historical catalogs show indexing progress without claiming zero log lines');
    await wait(() => logs >= 2);
    check(reports === 1 && images === 1, 'index polling retries only the resource that is still pending');
    visible.value = false; await nextTick(); const stoppedReads = reads;
    await new Promise(resolve => setTimeout(resolve, 1700));
    check(reads === stoppedReads, 'hidden history stops index polling');
    pending = false; visible.value = true; await nextTick();
    await wait(() => !!mount.querySelector('.history-log-toggle'));
    check(!mount.querySelector('[role="status"]'), 'available catalogs replace indexing progress with actual history');
  } finally { app.unmount(); mount.remove(); owner.dispose(); }
}
