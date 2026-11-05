import { FrontendSession } from './frontend-session';

export class ControlFailure extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    readonly correlationId?: string,
    readonly operationId?: string,
    readonly uncertain = false
  ) {
    super(code);
  }
}

export type Decoder<T> = (value: unknown) => T;

export class ControlClient {
  private token = '';
  private writes = 0;
  private restarting = false;
  private readonly frontendSession: FrontendSession;
  constructor(
    private readonly origin = location.origin,
    private readonly send: typeof fetch = globalThis.fetch.bind(globalThis)
  ) { this.frontendSession = new FrontendSession(origin, send, () => this.headers(), typeof window === 'undefined' ? undefined : window.nexusDesktopChannel); }
  get pendingWrites(): number {
    return this.writes;
  }
  // The restart response waits for native exit, so its own RPC cannot block that signed handoff.
  canExitMaintenance(): boolean {
    return this.writes === (this.restarting ? 1 : 0);
  }
  authenticate(token: string): void {
    this.frontendSession.reset();
    this.token = token;
  }
  headers(): Headers {
    const headers = new Headers({ Accept: 'application/json' });
    if (this.token) headers.set('Authorization', 'Bearer ' + this.token);
    return headers;
  }
  async read<T>(path: string, decode: Decoder<T>, signal?: AbortSignal): Promise<T> {
    return decode(await this.request(path, undefined, signal));
  }
  async query<T>(path: string, body: unknown, decode: Decoder<T>, signal?: AbortSignal): Promise<T> {
    return decode(await this.request(path, body, signal, undefined, false));
  }
  async write<T>(path: string, body: unknown, decode: Decoder<T>, operationId?: string, signal?: AbortSignal): Promise<T> {
    this.writes++;
    try {
      const response = await this.request(path, body, signal, operationId);
      try {
        return decode(response);
      } catch {
        throw new ControlFailure(200, 'control_response_invalid', undefined, operationId, true);
      }
    } finally {
      this.writes--;
    }
  }
  // Local secret writes use a separate raw transport but must block maintenance exit while unsettled.
  async trackExternalWrite<T>(send: () => Promise<T>): Promise<T> {
    this.writes++;
    try {
      return await send();
    } finally {
      this.writes--;
    }
  }
  async restartHost<T>(operationId: string, decode: Decoder<T>): Promise<T> {
    if (
      this.restarting ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(operationId)
    )
      throw new Error('host_restart_unavailable');
    this.restarting = true;
    try {
      return await this.write('/control/v1/maintenance/restart/' + operationId, null, decode, operationId);
    } finally {
      this.restarting = false;
    }
  }
  async response(path: string, signal?: AbortSignal): Promise<Response> {
    if (!path.startsWith('/control/v1/') || path.includes('..') || path.includes('\\'))
      throw new Error('control_path_invalid');
    const response = await this.send(new URL(path, this.origin), {
      headers: this.headers(),
      cache: 'no-store',
      ...(signal ? { signal } : {})
    });
    if (!response.ok) throw await this.failure(response);
    return response;
  }
  async upload(path: string, file: File, operationId: string, signal: AbortSignal): Promise<unknown> {
    if (!path.startsWith('/control/v1/extensions/') || path.includes('..') || path.includes('\\') ||
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(operationId) ||
        file.size < 1 || file.size > 8388608 || !['image/png', 'image/jpeg', 'image/webp'].includes(file.type))
      throw new Error('control_upload_invalid');
    signal.throwIfAborted();
    return this.trackExternalWrite(async () => {
      const headers = path.includes('/frontend/') ? await this.frontendSession.headers(path, signal) : this.headers();
      headers.set('Content-Type', file.type); headers.set('X-Nexus-Operation-Id', operationId);
      headers.set('X-Nexus-File-Name', encodeURIComponent(file.name));
      try {
        const response = await this.send(new URL(path, this.origin), { method: 'POST', headers, body: file,
          cache: 'no-store', signal: AbortSignal.any([signal, AbortSignal.timeout(30000)]) });
        if (!response.ok) throw await this.failure(response, operationId);
        return JSON.parse(new TextDecoder().decode(await boundedBytes(response, 1048576)));
      } catch (error) {
        if (error instanceof ControlFailure) throw error;
        throw new ControlFailure(0, 'write_outcome_unknown', undefined, operationId, true);
      }
    });
  }
  private async request(
    path: string,
    body?: unknown,
    signal?: AbortSignal,
    operationId?: string,
    isWrite = body !== undefined
  ): Promise<unknown> {
    if (!path.startsWith('/control/v1/') || path.includes('..') || path.includes('\\'))
      throw new Error('control_path_invalid');
    const headers = path.includes('/frontend/') ? await this.frontendSession.headers(path, signal) : this.headers();
    const deadline = AbortSignal.timeout(path.includes('/frontend/') && path.includes('/invoke/') ? 360000 : 30000);
    if (body !== undefined) headers.set('Content-Type', 'application/json');
    try {
      const response = await this.send(new URL(path, this.origin), {
        method: body === undefined ? 'GET' : 'POST',
        headers,
        cache: 'no-store',
        ...(body === undefined || body === null ? {} : { body: JSON.stringify(body) }),
        signal: signal ? AbortSignal.any([signal, deadline]) : deadline
      });
      if (!response.ok) throw await this.failure(response, operationId);
      if (response.status === 204) return null;
      const text = new TextDecoder().decode(await boundedBytes(response, 16 * 1024 * 1024));
      try {
        return JSON.parse(text);
      } catch {
        throw new ControlFailure(
          response.status,
          'control_response_invalid',
          undefined,
          operationId,
          isWrite
        );
      }
    } catch (error) {
      if (error instanceof ControlFailure || signal?.aborted && !isWrite) throw error;
      throw new ControlFailure(
        0,
        isWrite ? 'write_outcome_unknown' : 'connection_lost',
        undefined,
        operationId,
        isWrite
      );
    }
  }
  private async failure(response: Response, operationId?: string): Promise<ControlFailure> {
    try {
      const value = objectValue(JSON.parse(new TextDecoder().decode(await boundedBytes(response, 65536))));
      return new ControlFailure(
        response.status,
        stringValue(value.code),
        optionalString(value.correlationId),
        operationId
      );
    } catch {
      return new ControlFailure(response.status, 'control_request_failed', undefined, operationId);
    }
  }
}

export async function boundedBytes(response: Response, maximum: number): Promise<Uint8Array> {
  const reader = response.body?.getReader();
  if (!reader) return new Uint8Array();
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      const next = await reader.read();
      if (next.done) break;
      size += next.value.byteLength;
      if (size > maximum) throw new Error('control_response_limit');
      chunks.push(next.value);
    }
  } finally {
    await reader.cancel();
    reader.releaseLock();
  }
  const result = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.length;
  }
  return result;
}

export function objectValue(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new TypeError('control_object_invalid');
  return value as Record<string, unknown>;
}
export function stringValue(value: unknown): string {
  if (typeof value !== 'string') throw new TypeError('control_string_invalid');
  return value;
}
export function optionalString(value: unknown): string | undefined {
  return value == null ? undefined : stringValue(value);
}
export function integerValue(value: unknown): number {
  if (!Number.isSafeInteger(value) || Number(value) < 0) throw new TypeError('control_integer_invalid');
  return Number(value);
}
export function arrayValue<T>(value: unknown, decode: Decoder<T>): T[] {
  if (!Array.isArray(value)) throw new TypeError('control_array_invalid');
  return value.map(decode);
}

export function booleanValue(value: unknown): boolean {
  if (typeof value !== 'boolean') throw new TypeError('control_boolean_invalid');
  return value;
}
export function enumValue<const T extends readonly string[]>(value: unknown, choices: T): T[number] {
  const text = stringValue(value);
  if (!choices.includes(text)) throw new TypeError('control_enum_invalid');
  return text;
}
export function boundedInteger(value: unknown, minimum: number, maximum: number): number {
  if (!Number.isSafeInteger(value) || Number(value) < minimum || Number(value) > maximum)
    throw new TypeError('control_integer_invalid');
  return Number(value);
}
export function uuidValue(value: unknown): string {
  const text = stringValue(value);
  if (!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(text))
    throw new TypeError('control_identity_invalid');
  return text;
}
export function dateValue(value: unknown): string {
  const text = stringValue(value);
  if (!/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)$/.test(text) || !Number.isFinite(Date.parse(text)))
    throw new TypeError('control_date_invalid');
  return text;
}
