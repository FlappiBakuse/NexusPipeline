import { createApp, h, nextTick, ref } from 'vue';
import LiveLogPanel from '../../apps/web/src/components/LiveLogPanel.vue';
import type { LiveLogs, LogIdentity, LogWindow } from '../../apps/web/src/control/live-logs';

export async function logPanelContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const subscriptions: { key: LogIdentity; send: (value: LogWindow) => void; released: boolean }[] = [];
  const subscribe: LiveLogs['subscribe'] = (key, send) => {
    const owner = { key, send, released: false }; subscriptions.push(owner);
    return () => { owner.released = true; };
  };
  const visible = ref(true);
  const identity = ref({ runId: crypto.randomUUID(), attemptId: crypto.randomUUID(), stepId: 'first' });
  const mount = document.createElement('section');
  mount.style.cssText = 'position:fixed;top:0;left:0;width:500px;height:250px;z-index:1000';
  fixture.append(mount);
  const app = createApp({ render: () => h(LiveLogPanel, { subscribe, identity: identity.value, roles: ['main'], visible: visible.value }) });
  app.mount(mount);
  const wait = async (ready: () => boolean) => {
    for (let count = 0; count < 100 && !ready(); count++) await new Promise(resolve => setTimeout(resolve, 20));
    check(ready(), 'the visible log panel settles');
  };
  const frame = (text: string, sequence: number): LogWindow => ({ text, sequence, connected: true, omitted: false });
  let paints = 0;
  const changes = new MutationObserver(() => { paints++; });
  try {
    await wait(() => subscriptions.length === 1);
    const first = subscriptions[0]!;
    const pre = mount.querySelector('pre')!;
    changes.observe(pre, { childList: true, characterData: true, subtree: true });
    let text = '';
    for (let line = 1; line <= 500; line++) {
      text += (line === 1 ? '' : '\n') + `line-${line}`;
      first.send(frame(text, line));
      if (line % 25 === 0) await new Promise(resolve => setTimeout(resolve, 1));
    }
    await wait(() => pre.textContent === text);
    check(pre.textContent?.split('\n').length === 500, 'batched live output preserves all five hundred log lines');
    check(paints < 50, 'a burst of five hundred frames causes bounded visible log updates');
    first.send(frame('pending-old-run', 501));
    identity.value = { ...identity.value, stepId: 'second' };
    await nextTick();
    await wait(() => subscriptions.length === 2);
    check(first.released, 'changing the step releases the previous log stream');
    first.send(frame('late-old-run', 502));
    const second = subscriptions[1]!;
    second.send(frame('current-run', 1));
    await wait(() => pre.textContent === 'current-run');
    second.send(frame('pending-hidden', 2));
    visible.value = false; await nextTick();
    check(second.released, 'a hidden log panel releases its stream');
    await new Promise(resolve => setTimeout(resolve, 150));
    check(pre.textContent === 'current-run', 'hiding cancels a pending log paint');
    visible.value = true; await nextTick();
    await wait(() => subscriptions.length === 3);
    const third = subscriptions[2]!;
    third.send(frame('resumed-run', 3));
    await wait(() => pre.textContent === 'resumed-run');
    mount.style.top = '-2000px';
    await wait(() => third.released);
    check(subscriptions.length === 3, 'offscreen log panels leave no active stream');
  } finally {
    changes.disconnect(); app.unmount(); mount.remove();
  }
  check(subscriptions.every(owner => owner.released), 'unmounting releases all log subscriptions');
}
