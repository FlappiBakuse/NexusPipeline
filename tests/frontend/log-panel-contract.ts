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
  const roles = ref(['main']);
  const identity = ref({ runId: crypto.randomUUID(), attemptId: crypto.randomUUID(), stepId: 'first' });
  const mount = document.createElement('section');
  mount.style.cssText = 'position:fixed;top:0;left:0;width:500px;height:250px;z-index:1000';
  fixture.append(mount);
  const app = createApp({ render: () => h(LiveLogPanel, { subscribe, identity: identity.value, roles: roles.value, visible: visible.value }) });
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
    third.send({ ...frame('resumed-run', 3), connected: false, error: 'log_event_invalid' });
    await wait(() => !!mount.querySelector('[role="status"]')?.textContent?.includes('log_event_invalid'));
    visible.value = false; await nextTick();
    visible.value = true; await nextTick();
    await wait(() => subscriptions.length === 4);
    const fourth = subscriptions[3]!;
    fourth.send({ text: '', sequence: 0, connected: false, omitted: false });
    await wait(() => !mount.querySelector('[role="status"]'));
    check(pre.textContent === 'resumed-run', 'reopening the same role clears a stale log error and preserves prior text');
    roles.value = ['postHook', 'main', 'preHook']; await nextTick();
    await wait(() => subscriptions.length === 7);
    check(fourth.released, 'changing selected roles releases the former subscription');
    const grouped = subscriptions.slice(4);
    const send = (role: string, text: string, sequence: number) => grouped.find(owner => owner.key.role === role)!.send(frame(text, sequence));
    const renderedText = () => [...pre.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.nodeValue).join('');
    const order = ['preHook', 'main', 'postHook'];
    const strings = order.map(role => Array.from({ length: 200 }, (_item, index) => role + '-' + index + '\n').join(''));
    order.forEach((role, index) => send(role, strings[index]!, 1));
    const expectedLines = strings.join('').split('\n').slice(-500).join('\n');
    await wait(() => renderedText() === expectedLines);
    check(renderedText() === expectedLines, 'the combined roles retain the final five hundred lines in canonical role order');
    const longPre = '😀' + 'a'.repeat(256 * 1024 - 36);
    const main = 'm'.repeat(16), post = 'p'.repeat(17);
    send('preHook', longPre, 2); send('main', main, 2); send('postHook', post, 2);
    const expectedUnits = [longPre.slice(2), main, post].join('\n');
    await wait(() => renderedText().length > 250000 && renderedText().endsWith(post));
    check(renderedText() === expectedUnits && pre.querySelector('.omitted'), 'the combined roles keep a separate character limit without splitting a code point');
    const filters = [...mount.querySelectorAll<HTMLButtonElement>('.script-log-roles button')];
    check(filters.map(button => button.textContent?.trim()).join(',') === '全部,前置,主任务,后置',
      'live log roles are offered in invocation order');
    filters[1]!.click();
    await wait(() => renderedText() === longPre);
    check(filters[1]!.getAttribute('aria-pressed') === 'true' && grouped.every(owner => !owner.released),
      'switching the displayed log role reuses the focused run streams');
    mount.style.top = '-2000px';
    await wait(() => grouped.every(owner => owner.released));
    check(subscriptions.length === 7, 'offscreen log panels leave no active stream');
  } finally {
    changes.disconnect(); app.unmount(); mount.remove();
  }
  check(subscriptions.every(owner => owner.released), 'unmounting releases all log subscriptions');
}
