import { createIdentity } from '../control/identity';
import { markRaw, reactive } from 'vue';
import { InterfaceChanges } from './interface-changes';
import { AppSession } from './app-session';
import { HostSnapshots } from './host-snapshots';
import { createHostState } from './host-state';
import { routePage } from './navigation';
import { ControlClient, ControlFailure, type Decoder } from '../control/client';
import { PreparedReadRevoked, type EditorDirectory } from '../control/control-reads';
import { acceptedCatalogRevision } from '../control/automation';
import type { ControlAutomationChange } from '../control/wire.generated';
import type { InterfaceValues } from '../control/interface';
import { MediaPool } from '../control/media-pool';
import { LiveLogs } from '../control/live-logs';
import { EditorDraftStore } from './editor-drafts';

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
  const interfaces = new InterfaceChanges(client, state);
  const editors = markRaw(new EditorDraftStore(report));
  const snapshots = new HostSnapshots(client, state, interfaces);
  const session = new AppSession(client, state, snapshots, report, routePage);
  const directoryAccess = () => JSON.stringify([state.capabilities?.instanceId, state.capabilities?.hostVersion,
    state.capabilities?.capabilities.toSorted()]);
  const directoryScope = (name: EditorDirectory) => {
    const mutation = state.events?.mutationSequence, activation = state.events?.extensionGeneration;
    const reusable = state.visible && !state.feedError && Number.isSafeInteger(mutation) &&
      (name === 'notifications' || typeof activation === 'string');
    const key = JSON.stringify([snapshots.streamId, directoryAccess(), mutation, name === 'extensions' ? activation : null, reusable]);
    return { key, reusable };
  };
  const readEditorDirectory = async <T>(name: EditorDirectory, decode: Decoder<T>, signal: AbortSignal): Promise<T> => {
    if (!state.capabilities?.capabilities.includes(name + '.read')) throw new ControlFailure(403, 'control_capability_denied');
    const authority = client.authoritySignal, access = directoryAccess();
    for (let attempt = 0; attempt < 2; attempt++) {
      signal.throwIfAborted();
      authority.throwIfAborted();
      if (directoryAccess() !== access) throw new PreparedReadRevoked();
      const scope = directoryScope(name);
      try {
        const value = await client.readEditorDirectory(name, scope.key, scope.reusable, decode, signal);
        signal.throwIfAborted();
        authority.throwIfAborted();
        if (directoryScope(name).key === scope.key) return value;
      } catch (error) {
        if (!(error instanceof PreparedReadRevoked) || directoryScope(name).key === scope.key) throw error;
      }
    }
    throw new PreparedReadRevoked();
  };
  const prepareEditorDirectory = async <T>(name: EditorDirectory, decode: Decoder<T>, signal: AbortSignal): Promise<T> => {
    const scope = directoryScope(name), authority = client.authoritySignal;
    const value = await client.prepareRead('/control/v1/' + name, signal, decode, scope.key);
    authority.throwIfAborted();
    if (directoryScope(name).key !== scope.key) throw new PreparedReadRevoked();
    return value;
  };
  const change = async (body: ControlAutomationChange): Promise<void> => {
    const authority = client.authoritySignal;
    const accepted = await client.write('/control/v1/automation/change', body, value => acceptedCatalogRevision(value, body),
      body.operationId, authority);
    await snapshots.refreshCatalog(accepted, authority);
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
    editors,
    report,
    change,
    command,
    refresh: (signal?: AbortSignal) => snapshots.refresh(signal),
    refreshCatalog: (accepted: number, signal?: AbortSignal) => snapshots.refreshCatalog(accepted, signal),
    refreshSettings: (accepted?: number, signal?: AbortSignal) => snapshots.refreshSettings(accepted, signal),
    readEditorDirectory,
    prepareEditorDirectory,
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
    dispose: () => { session.dispose(); editors.dispose(); drafts.clear(); dirty.clear(); }
  };
}

export type AppStore = ReturnType<typeof createStore>;
