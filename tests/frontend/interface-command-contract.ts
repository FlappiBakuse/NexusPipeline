import { InterfaceChanges } from '../../apps/web/src/app/interface-changes.js';
import { ControlClient } from '../../apps/web/src/control/client.js';
import type { InterfaceSnapshot } from '../../apps/web/src/control/interface.js';

type Check = (condition: unknown, name: string) => void;
const response = (value: unknown): Response => new Response(JSON.stringify(value), { status: 200 });
const id = (end: string): string => 'c0000000-0000-4000-8000-00000000000' + end;
function snapshot(revision = 0, operationId = id('0'), theme: 'dark' | 'light' = 'dark'): InterfaceSnapshot {
  return { revision, operationId, values: { theme, locale: 'zh-CN', dashboardCards: ['status', 'running'],
    pinnedSettings: [], collapsedSettings: ['remote', 'proxy'] } };
}

export async function interfaceCommandContract(check: Check): Promise<void> {
  await unknownResult(check);
  await overlappingProof(check);
  await changedInstance(check);
}

async function unknownResult(check: Check): Promise<void> {
  const state = { interface: snapshot(), capabilities: { instanceId: id('1') }, uncertainOperation: '', error: '' };
  const drafts = new Map<string, Record<string, unknown>>(), dirty = new Set<string>();
  let writes = 0, read = snapshot();
  const client = new ControlClient('http://127.0.0.1', async (_url, request) => {
    if (request?.method === 'POST') { writes++; return response({ revision: 1 }); }
    return response(read);
  });
  const changes = new InterfaceChanges(client, state, drafts, dirty), values = snapshot(1, id('2'), 'light').values;
  const write = changes.save(values, 0, id('2')).catch(() => undefined);
  check(changes.current().pending?.id === id('2') && dirty.size === 1, 'interface command owns its original body before dispatch');
  await write;
  check(changes.current().pending?.state === 'unknown', 'valid JSON with a malformed interface write result keeps its original command');
  await changes.save(values, 0, id('3')).catch(() => undefined);
  check(writes === 1, 'another interface write cannot replay or replace an unresolved command');
  read = snapshot(2, id('3'), 'light'); await changes.inspect();
  check(changes.current().pending?.id === id('2'), 'equal interface values from another operation are not completion proof');
  changes.observe(snapshot(1, id('2'), 'light'));
  check(changes.current().pending === null && state.interface.revision === 2 && dirty.size === 0,
    'a delayed exact operation proof unlocks without regressing a newer interface snapshot');
}

async function overlappingProof(check: Check): Promise<void> {
  const state = { interface: snapshot(), capabilities: { instanceId: id('1') }, uncertainOperation: '', error: '' };
  const replies = new Map<string, (value: Response) => void>(), drafts = new Map<string, Record<string, unknown>>();
  const dirty = new Set<string>();
  const client = new ControlClient('http://127.0.0.1', async (_url, request) => {
    const body = JSON.parse(String(request?.body)) as { operationId: string };
    return new Promise<Response>(resolve => replies.set(body.operationId, resolve));
  });
  const changes = new InterfaceChanges(client, state, drafts, dirty);
  const first = changes.save(snapshot(1, id('2'), 'light').values, 0, id('2'));
  changes.observe(snapshot(1, id('2'), 'light'));
  const second = changes.save(snapshot(2, id('3')).values, 1, id('3'));
  replies.get(id('2'))!(response({ malformed: true }));
  const confirmed = await first;
  check(confirmed.operationId === id('2') && changes.current().pending?.id === id('3'),
    'a proven late first result cannot clear a newer interface command');
  replies.get(id('3'))!(response(snapshot(2, id('3')))); await second;
  check(changes.current().pending === null && state.interface.operationId === id('3') && dirty.size === 0,
    'the newer interface command retains ownership until its own result arrives');
}

async function changedInstance(check: Check): Promise<void> {
  const state = { interface: snapshot(), capabilities: { instanceId: id('1') }, uncertainOperation: '', error: '' };
  let reply: (value: Response) => void = () => undefined;
  const client = new ControlClient('http://127.0.0.1', async () => new Promise<Response>(resolve => { reply = resolve; }));
  const changes = new InterfaceChanges(client, state, new Map(), new Set());
  const first = changes.save(snapshot(1, id('2'), 'light').values, 0, id('2'));
  state.capabilities = { instanceId: id('4') }; state.interface = snapshot();
  reply(response(snapshot(1, id('2'), 'light'))); await first;
  check(state.interface.revision === 0 && state.interface.values.theme === 'dark' && changes.current().pending === null,
    'an old instance result cannot publish preferences or pending state into a different instance');
}
