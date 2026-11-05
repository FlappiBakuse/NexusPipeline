import { createIdentity } from '../control/identity';
import { boundedBytes, ControlFailure, objectValue, type ControlClient } from '../control/client.js';

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
      field === 'control-bearer' ? '/control/v1/local/control-token/change' : path(field) + '/change';
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
