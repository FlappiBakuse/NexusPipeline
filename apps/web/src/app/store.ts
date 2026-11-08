import { createIdentity } from '../control/identity';
import { reactive } from 'vue';
import { InterfaceChanges } from './interface-changes';
import { AppSession } from './app-session';
import { HostSnapshots } from './host-snapshots';
import { createHostState } from './host-state';
import { routePage } from './navigation';
import { ControlClient, ControlFailure, objectValue } from '../control/client';
import type { ControlAutomationChange } from '../control/wire.generated';
import type { InterfaceValues } from '../control/interface';
import { MediaPool } from '../control/media-pool';
import { LiveLogs } from '../control/live-logs';

export { navigation, routePage } from './navigation';
export type { PageId, AppRoute } from './navigation';

export function createStore(client: ControlClient) {
  const state = createHostState();
  const drafts = reactive(new Map<string, Record<string, unknown>>());
  const dirty = reactive(new Set<string>());
  const report = (error: unknown): void => {
    if (error instanceof DOMException && error.name === 'AbortError') return;
    state.error = error instanceof Error ? error.message : 'request_failed';
    if (error instanceof ControlFailure && error.uncertain)
      state.uncertainOperation = error.operationId || '';
  };
  const interfaces = new InterfaceChanges(client, state, drafts, dirty);
  const snapshots = new HostSnapshots(client, state, interfaces);
  const session = new AppSession(client, state, snapshots, report, routePage);
  const change = async (body: ControlAutomationChange): Promise<void> => {
    await client.write('/control/v1/automation/change', body, objectValue, body.operationId);
    await snapshots.refresh();
  };
  const command = (): ControlAutomationChange => ({
    format: 'nexus.control.automation-change',
    version: 1,
    operationId: createIdentity(),
    expectedCatalogRevision: state.automation?.catalogRevision || 0,
    roots: [],
    scripts: [],
    users: [],
    bindings: [],
    queues: [],
    schedules: [],
    removed: []
  });
  return {
    client,
    state,
    drafts,
    dirty,
    report,
    change,
    command,
    refresh: (signal?: AbortSignal) => snapshots.refresh(signal),
    navigate: session.navigate.bind(session),
    saveInterface: (values: InterfaceValues, expectedRevision: number, operationId = createIdentity()) =>
      interfaces.save(values, expectedRevision, operationId),
    inspectInterface: () => interfaces.inspect(),
    get interfaceChange() {
      return interfaces.current();
    },
    media: new MediaPool(client),
    logs: new LiveLogs(client),
    can: (capability: string) => state.capabilities?.capabilities.includes(capability) === true,
    start: (signal?: AbortSignal) => session.start(signal),
    dispose: () => session.dispose()
  };
}

export type AppStore = ReturnType<typeof createStore>;
