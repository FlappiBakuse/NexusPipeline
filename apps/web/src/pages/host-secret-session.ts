import { createIdentity } from '../control/identity';
import type { AppStore } from '../app/store';
import { ControlFailure, objectValue, boundedBytes, type ControlClient } from '../control/client.js';
import { reactive } from 'vue';

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
const sessions = new WeakMap<AppStore, Map<string, HostSecretSession>>();

export function hostSecretSession(store: AppStore, instance: string, field: LocalField): HostSecretSession {
  const id = key(instance, field);
  let values = sessions.get(store);
  if (!values) {
    values = new Map(); sessions.set(store, values);
    store.editors.signal.addEventListener('abort', () => values!.clear(), { once: true });
  }
  if (!values.has(id)) values.set(id, reactive({ pending: null, status: null, error: '', operationStatus: '' }));
  return values.get(id)!;
}

function accept(
  store: AppStore,
  instance: string,
  field: LocalField,
  id: string,
  result: LocalSecretStatus
): void {
  if (store.editors.signal.aborted) return;
  const state = hostSecretSession(store, instance, field);
  if (state.pending?.id !== id) return;
  if (!state.status || result.revision >= state.status.revision) state.status = result;
  state.pending = null;
  state.error = '';
  state.operationStatus = '';
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
  try {
    const request = changeValue(store.client, field, revision, disposition, value, id);
    value = '';
    const result = await request;
    accept(store, instance, field, id, result);
    return true;
  } catch (reason) {
    if (reason instanceof ControlFailure && reason.uncertain) {
      state.pending!.state = 'unknown';
      state.error = '秘密变更结果待确认。';
      for (const delay of [0, 200, 800, 2000]) {
        if (delay) await new Promise<void>(resolve => setTimeout(resolve, delay));
        if (store.editors.signal.aborted || !state.pending) break;
        await inspectHostSecret(store, instance, field, store.editors.signal);
      }
      if (!state.pending) return true;
      if (!state.error) state.error = '秘密变更结果待确认。';
    } else {
      state.pending = null;
      state.error = reason instanceof Error ? reason.message : '秘密保存失败';
      if (reason instanceof ControlFailure && reason.status === 409) state.status = null;
    }
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

export type LocalField = 'proxy-password' | 'control-bearer';
export interface LocalSecretStatus {
  revision: number;
  isConfigured: boolean;
  isReadable: boolean;
}
const path = (field: LocalField): string => '/control/v1/local/secrets/' + field;
export async function readStatus(
  client: ControlClient,
  field: LocalField,
  signal?: AbortSignal
): Promise<LocalSecretStatus> {
  const value = await client.read(path(field), objectValue, signal);
  if (
    !Number.isSafeInteger(value.revision) ||
    Number(value.revision) < 0 ||
    typeof value.isConfigured !== 'boolean' ||
    typeof value.isReadable !== 'boolean'
  )
    throw new TypeError('secret_status_invalid');
  return {
    revision: Number(value.revision),
    isConfigured: value.isConfigured === true,
    isReadable: value.isReadable === true
  };
}
export async function readValue(
  client: ControlClient,
  field: LocalField,
  signal?: AbortSignal
): Promise<string> {
  const response = await fetch(path(field) + '/read', {
    method: 'POST',
    headers: client.headers(),
    cache: 'no-store',
    signal
  });
  if (!response.ok) throw new Error(`secret_read_${response.status}`);
  return new TextDecoder('utf-8', { fatal: true }).decode(await boundedBytes(response, 196608));
}
export async function changeValue(
  client: ControlClient,
  field: LocalField,
  revision: number,
  mode: 'set' | 'clear',
  value: string,
  operationId = createIdentity()
): Promise<LocalSecretStatus> {
  return client.trackExternalWrite(async () => {
    const headers = client.headers();
    headers.set('Nexus-Operation-Id', operationId);
    headers.set('Nexus-Expected-Revision', String(revision));
    headers.set('Nexus-Secret-Disposition', mode);
    if (mode === 'set') headers.set('Content-Type', 'text/plain; charset=utf-8');
    const target =
      field === 'control-bearer' ? '/control/v1/local/control-token/change-from-settings' : path(field) + '/change';
    let response: Response;
    try {
      response = await fetch(target, {
        method: 'POST',
        headers,
        cache: 'no-store',
        body: mode === 'set' ? value : undefined,
        signal: AbortSignal.timeout(30000)
      });
    } catch {
      throw new ControlFailure(0, 'write_outcome_unknown', undefined, operationId, true);
    }
    if (!response.ok) {
      let code = 'secret_change_failed';
      try {
        code = String(
          objectValue(JSON.parse(new TextDecoder().decode(await boundedBytes(response, 65536)))).code || code
        );
      } catch {
        /* Preserve the HTTP failure if its response body is unavailable. */
      }
      throw new ControlFailure(response.status, code, undefined, operationId);
    }
    try {
      const result = objectValue(JSON.parse(new TextDecoder().decode(await boundedBytes(response, 65536))));
      if (
        result.operationId !== operationId ||
        result.revision !== revision + 1 ||
        typeof result.isConfigured !== 'boolean' ||
        typeof result.isReadable !== 'boolean'
      )
        throw new TypeError('secret_change_response_invalid');
      return {
        revision: Number(result.revision),
        isConfigured: result.isConfigured === true,
        isReadable: result.isReadable === true
      };
    } catch {
      throw new ControlFailure(0, 'write_outcome_unknown', undefined, operationId, true);
    }
  });
}
