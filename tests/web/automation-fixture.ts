import { ControlClient, ControlFailure } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';
import { automationSession } from '../../apps/web/src/pages/automation-session';
import { AutomationEditor } from '../../apps/web/src/pages/automation-editor';
import { AutomationCommands } from '../../apps/web/src/pages/automation-commands';
import { AutomationSave } from '../../apps/web/src/pages/automation-save';

export function fixture() {
  const store = createStore(new ControlClient('http://127.0.0.1'));
  store.state.capabilities = { instanceId: 'fixture', capabilities: ['automation.write', 'operations.read'] } as never;
  const queue = { id: crypto.randomUUID(), name: 'Queue', order: 0, scriptIds: [], completionAction: 'none', scheduleEnabled: true };
  const schedule = { id: crypto.randomUUID(), queueId: queue.id, enabled: true, weekdays: [1], localMinute: 330,
    timeZoneId: 'UTC', missedPolicy: 'skip' };
  let catalog = { catalogRevision: 1, roots: [], revisions: [{ kind: 'queue', id: queue.id, revision: 1 },
    { kind: 'schedule', id: schedule.id, revision: 1 }], bundle: { roots: [], scripts: [], users: [], bindings: [],
    queues: [queue], schedules: [schedule], attachments: [] } };
  store.state.automation = structuredClone(catalog) as never;
  const requests: any[] = [];
  let failure: 'none' | 'conflict' | 'unknown' | 'instance' = 'none';
  store.client.write = (async (_path: string, body: any, decode: (value: unknown) => unknown) => {
    requests.push(structuredClone(body));
    if (failure === 'conflict' || body.expectedCatalogRevision !== catalog.catalogRevision)
      throw new ControlFailure(409, 'revision_conflict', undefined, body.operationId);
    const savedQueue = body.queues[0];
    const prior = catalog.revisions.find(row => row.kind === 'queue' && row.id === savedQueue.value.id)?.revision || 0;
    if (savedQueue.expectedRevision !== prior) throw new ControlFailure(409, 'revision_conflict');
    for (const item of body.schedules) {
      const revision = catalog.revisions.find(row => row.kind === 'schedule' && row.id === item.value.id)?.revision || 0;
      if (item.expectedRevision !== revision) throw new ControlFailure(409, 'revision_conflict');
    }
    catalog = { ...catalog, catalogRevision: catalog.catalogRevision + 1,
      revisions: [{ kind: 'queue', id: savedQueue.value.id, revision: prior + 1 },
        ...body.schedules.map((item: any) => ({ kind: 'schedule', id: item.value.id, revision: item.expectedRevision + 1 }))],
      bundle: { ...catalog.bundle, queues: [structuredClone(savedQueue.value)],
        schedules: body.schedules.map((item: any) => structuredClone(item.value)) } };
    if (failure === 'unknown') throw new ControlFailure(200, 'control_response_invalid', undefined, body.operationId, true);
    if (failure === 'instance') store.state.capabilities = { ...store.state.capabilities, instanceId: 'other-instance' } as never;
    return decode({ operationId: body.operationId, catalogRevision: catalog.catalogRevision, isNew: true });
  }) as never;
  store.client.read = (async (_path: string, decode: (value: unknown) => unknown) => decode({
    operationId: requests.at(-1).operationId, kind: 'automation.change', status: 'completed'
  })) as never;
  store.refresh = async () => { store.state.automation = structuredClone(catalog) as never; };
  const session = automationSession(store, 'queues');
  const editor = new AutomationEditor(store, () => 'queues', session);
  const commands = new AutomationCommands(store, session, () => { editor.editing.value = false; });
  const save = new AutomationSave(store, editor, session, commands);
  return { store, queue, schedule, requests, session, editor, commands, save,
    fail(value: typeof failure) { failure = value; } };
}
