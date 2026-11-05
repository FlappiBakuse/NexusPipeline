type ClientGrant = { clientId: string; token: string; expiresAt: number };
type NativeFrontendBridge = { bindFrontend(clientId: string, token: string): Promise<unknown>; retireFrontend(clientId: string): Promise<void> };

export class FrontendSession {
  private grant?: ClientGrant;
  private pending?: Promise<ClientGrant>;
  private generation = 0;
  private owner = new AbortController();

  constructor(private readonly origin: string, private readonly send: typeof fetch,
    private readonly authorization: () => Headers, private readonly native?: NativeFrontendBridge) {}

  reset(): void {
    const previous = this.grant;
    const headers = this.authorization();
    this.generation++;
    this.owner.abort(); this.owner = new AbortController();
    this.grant = undefined; this.pending = undefined;
    if (previous) {
      void this.retire(previous, headers);
    }
  }

  async headers(path: string, signal?: AbortSignal): Promise<Headers> {
    const headers = this.authorization();
    if (!/^\/control\/v1\/extensions\/[^/]+\/frontend\/[^/]+\/(invoke|upload|project)\//.test(path)) return headers;
    signal?.throwIfAborted();
    const generation = this.generation;
    if (!this.grant || this.grant.expiresAt <= Date.now()) {
      if (!this.pending) {
        const pending = this.create(generation, this.owner.signal);
        this.pending = pending;
        void pending.finally(() => { if (this.pending === pending) this.pending = undefined; }).catch(() => {});
      }
      await this.wait(this.pending, signal);
    }
    signal?.throwIfAborted();
    if (generation !== this.generation || !this.grant) throw new Error('frontend_client_changed');
    headers.set('X-Nexus-Frontend-Client', this.grant.token);
    return headers;
  }

  private async wait(pending: Promise<ClientGrant>, signal?: AbortSignal): Promise<void> {
    if (!signal) { await pending; return; }
    await new Promise<void>((resolve, reject) => {
      const cancel = () => reject(signal.reason);
      signal.addEventListener('abort', cancel, { once: true });
      void pending.then(() => resolve(), reject).finally(() => signal.removeEventListener('abort', cancel));
      if (signal.aborted) cancel();
    });
  }

  private async create(generation: number, signal: AbortSignal): Promise<ClientGrant> {
    const authorization = this.authorization();
    const response = await this.send(new URL('/control/v1/extensions/clients', this.origin), {
      method: 'POST', headers: authorization, cache: 'no-store',
      signal: AbortSignal.any([signal, AbortSignal.timeout(15000)])
    });
    if (!response.ok || response.headers.get('content-length') && Number(response.headers.get('content-length')) > 1024)
      throw new Error('frontend_client_unavailable');
    const reader = response.body?.getReader();
    if (!reader) throw new Error('frontend_client_invalid');
    const chunks: Uint8Array[] = []; let length = 0;
    try {
      while (true) {
        const next = await reader.read();
        if (next.done) break;
        length += next.value.length;
        if (length > 1024) throw new Error('frontend_client_invalid');
        chunks.push(next.value);
      }
    } finally { await reader.cancel(); reader.releaseLock(); }
    const bytes = new Uint8Array(length); let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
    const value = JSON.parse(new TextDecoder().decode(bytes));
    const expiresAt = Date.parse(value.expiresAt);
    if (typeof value.token !== 'string' || !/^[a-f0-9]{64}$/.test(value.token) ||
        typeof value.clientId !== 'string' || !/^[a-f0-9-]{36}$/.test(value.clientId) ||
        !Number.isFinite(expiresAt) || expiresAt <= Date.now()) throw new Error('frontend_client_invalid');
    const grant = { clientId: value.clientId, token: value.token, expiresAt };
    try {
      if (generation !== this.generation || signal.aborted) throw new Error('frontend_client_changed');
      if (this.native) await this.native.bindFrontend(value.clientId, value.token);
      if (generation !== this.generation || signal.aborted) throw new Error('frontend_client_changed');
      return this.grant = grant;
    } catch (error) {
      await this.retire(grant, authorization); throw error;
    }
  }

  private async retire(grant: ClientGrant, authorization: Headers): Promise<void> {
    const headers = new Headers(authorization); headers.set('X-Nexus-Frontend-Client', grant.token);
    await Promise.allSettled([
      this.native?.retireFrontend(grant.clientId),
      this.send(new URL('/control/v1/extensions/clients/retire', this.origin), {
        method: 'POST', headers, cache: 'no-store', signal: AbortSignal.timeout(5000)
      })
    ]);
  }
}
