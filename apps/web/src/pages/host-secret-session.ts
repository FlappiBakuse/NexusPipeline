import { createIdentity } from '../control/identity';
import type { AppStore } from '../app/store';
import { ControlFailure, objectValue } from '../control/client.js';
import { changeValue, readStatus, type LocalField, type LocalSecretStatus } from './local-secrets.js';

type PendingSecret = {
  id: string;
  revision: number;
  disposition: 'set' | 'clear';
  state: 'sending' | 'unknown' | 'querying';
};
export type HostSecretSession = {
  pending: PendingSecret | null;
  status: LocalSecretStatus | null;
  error: string;
  operationStatus: string;
};
const key = (instance: string, field: LocalField): string => 'host-secret/' + instance + '/' + field;

export function hostSecretSession(store: AppStore, instance: string, field: LocalField): HostSecretSession {
  const id = key(instance, field);
  if (!store.drafts.has(id))
    store.drafts.set(id, { pending: null, status: null, error: '', operationStatus: '' });
  return store.drafts.get(id) as unknown as HostSecretSession;
}

function accept(
  store: AppStore,
  instance: string,
  field: LocalField,
  id: string,
  result: LocalSecretStatus
): void {
  const state = hostSecretSession(store, instance, field);
  if (state.pending?.id !== id) return;
  if (!state.status || result.revision >= state.status.revision) state.status = result;
  state.pending = null;
  state.error = '';
  state.operationStatus = '';
  store.dirty.delete(key(instance, field));
  if (store.state.uncertainOperation === id) {
    store.state.uncertainOperation = '';
    store.state.error = '';
  }
}

export async function writeHostSecret(
  store: AppStore,
  instance: string,
  field: LocalField,
  revision: number,
  disposition: 'set' | 'clear',
  value: string
): Promise<boolean> {
  const state = hostSecretSession(store, instance, field);
  if (state.pending || store.state.capabilities?.instanceId !== instance) return false;
  const id = createIdentity();
  state.pending = { id, revision, disposition, state: 'sending' };
  state.error = '';
  state.operationStatus = '';
  store.dirty.add(key(instance, field));
  try {
    const result = await changeValue(store.client, field, revision, disposition, value, id);
    accept(store, instance, field, id, result);
    return true;
  } catch (reason) {
    if (reason instanceof ControlFailure && reason.uncertain) {
      state.pending!.state = 'unknown';
      state.error = '秘密变更结果未知，请查询原操作。';
    } else {
      state.pending = null;
      store.dirty.delete(key(instance, field));
      state.error = reason instanceof Error ? reason.message : '秘密保存失败';
      if (reason instanceof ControlFailure && reason.status === 409) state.status = null;
    }
    if (store.state.capabilities?.instanceId === instance) store.report(reason);
    return false;
  }
}

export async function inspectHostSecret(
  store: AppStore,
  instance: string,
  field: LocalField,
  signal: AbortSignal
): Promise<void> {
  const state = hostSecretSession(store, instance, field),
    pending = state.pending;
  if (!pending || pending.state !== 'unknown' || store.state.capabilities?.instanceId !== instance) return;
  pending.state = 'querying';
  state.error = '';
  try {
    const path =
      field === 'control-bearer'
        ? '/control/v1/local/control-token/operations/'
        : '/control/v1/local/secrets/proxy-password/operations/';
    const operation = await store.client.read(path + pending.id, objectValue, signal);
    signal.throwIfAborted();
    if (
      operation.operationId !== pending.id ||
      (field === 'control-bearer' && typeof operation.isDurable !== 'boolean') ||
      (field === 'proxy-password' && operation.kind !== 'secret.change')
    )
      throw new Error('secret_operation_unbound');
    state.operationStatus = String(operation.status);
    if (operation.status !== 'completed' || (field === 'control-bearer' && !operation.isDurable)) return;
    const result = await readStatus(store.client, field, signal);
    signal.throwIfAborted();
    if (result.revision <= pending.revision) throw new Error('secret_operation_revision_unbound');
    accept(store, instance, field, pending.id, result);
  } catch (reason) {
    if (!signal.aborted) state.error = reason instanceof Error ? reason.message : '秘密操作查询失败';
  } finally {
    if (state.pending?.id === pending.id) state.pending.state = 'unknown';
  }
}
