import {
  contractFingerprint, sdkVersion, type ExtensionManifest, type FrontendExtension,
  type FrontendTransport, type ExtensionScope
} from '@nexuspipeline/extension-sdk';
import { ControlClient, arrayValue, boundedBytes, objectValue, stringValue } from '../control/client';
import type { MediaPool } from '../control/media-pool';
import type { FrontendRegistry } from './frontend-registry';
import { InstalledTransport } from './frontend-transport';
import { abortable } from '../control/abortable';
import { retryRead } from '../control/read-retry';

interface InstalledModule {
  readonly activationId: string;
  readonly manifest: ExtensionManifest;
}
interface LoadedModule {
  readonly activationId: string;
  readonly controller: AbortController;
  readonly scope: ExtensionScope;
  readonly transport: InstalledTransport;
}
interface BrowserModule {
  createFrontend(transport: FrontendTransport): FrontendExtension;
}

export class FrontendLoader {
  private readonly active = new Map<string, LoadedModule>();
  private readonly lifetime = new AbortController();
  private connection = new AbortController();
  private retirement: Promise<void> = Promise.resolve();
  private work: Promise<void> | undefined;
  private changed = false;
  private halted = false;
  private readonly pending = new Set<AbortController>();
  private batch = new AbortController();
  progress: (done: number, total: number) => void = () => {};

  cancelPending(): void {
    this.halted = true;
    this.batch.abort();
    this.changed = false;
    for (const controller of this.pending) controller.abort(new Error('extension_preparation_timeout'));
  }

  constructor(private readonly client: ControlClient, private readonly media: MediaPool,
    private readonly registry: FrontendRegistry, private readonly report: (error: unknown) => void) {}

  async synchronize(): Promise<void> {
    this.halted = false;
    if (this.batch.signal.aborted) this.batch = new AbortController();
    if (this.connection.signal.aborted) this.connection = new AbortController();
    this.changed = true;
    while (this.changed || this.work) {
      if (!this.work) this.work = this.drain().finally(() => { this.work = undefined; });
      await this.work;
      if (this.halted || this.connection.signal.aborted || this.lifetime.signal.aborted) return;
    }
  }

  disconnect(): void {
    this.cancelPending();
    this.connection.abort();
    const scopes = [...this.active.values()];
    for (const module of scopes) {
      module.controller.abort();
    }
    this.active.clear();
    this.retirement = Promise.all([this.retirement, ...scopes.map(module => module.scope.dispose())])
      .then(() => {}).catch(this.report);
  }

  notify(): void {
    for (const module of this.active.values()) module.transport.notify();
  }

  async dispose(): Promise<void> {
    this.lifetime.abort();
    for (const module of this.active.values()) module.controller.abort();
    await this.work?.catch(() => {});
    await this.retirement;
    await Promise.all([...this.active.values()].map(module => module.scope.dispose()));
    this.active.clear();
  }

  private async drain(): Promise<void> {
    await this.retirement;
    while (this.changed && !this.lifetime.signal.aborted) {
      this.changed = false;
      const connection = AbortSignal.any([this.lifetime.signal, this.connection.signal]);
      const signal = AbortSignal.any([connection, this.batch.signal]);
      let catalog: Record<string, unknown>;
      try { catalog = await this.client.read('/control/v1/extensions/frontend', objectValue, signal); }
      catch (error) { if (signal.aborted) return; throw error; }
      if (signal.aborted) return;
      const kind = stringValue(catalog.connectionKind);
      if (kind !== 'local' && kind !== 'remote') throw new Error('extension_connection_invalid');
      const entries = arrayValue(catalog.extensions, readModule);
      if (entries.length > 64 || new Set(entries.map(entry => entry.manifest.extensionId)).size !== entries.length)
        throw new Error('extension_module_catalog_invalid');
      const installed = new Map(entries.map(entry => [entry.manifest.extensionId, entry]));
      for (const [id, module] of this.active) {
        if (installed.get(id)?.activationId === module.activationId) continue;
        module.controller.abort();
        await module.scope.dispose();
        this.active.delete(id);
      }
      const pending = entries.filter(entry => !this.active.has(entry.manifest.extensionId));
      let next = 0, done = 0;
      const progress = this.progress;
      progress(0, pending.length);
      await Promise.all(Array.from({ length: Math.min(6, pending.length) }, async () => {
        while (next < pending.length && !signal.aborted && !this.halted) {
          const entry = pending[next++]!;
          try { await retryRead(() => this.load(entry, kind, connection), signal); }
          catch (error) { if (!signal.aborted) this.report(error); }
          finally { if (!signal.aborted) progress(++done, pending.length); }
        }
      }));
    }
  }

  private async load(entry: InstalledModule, kind: 'local' | 'remote', connection: AbortSignal): Promise<void> {
    const id = entry.manifest.extensionId;
    const controller = new AbortController();
    this.pending.add(controller);
    const signal = AbortSignal.any([controller.signal, this.lifetime.signal, connection]);
    const base = `/control/v1/extensions/${id}/frontend/${entry.activationId}/assets`;
    let url: string | undefined;
    try {
      const { bytes, styles } = await this.profile(id, 'resources', async () => {
        const response = await this.client.response(base, signal);
        const decoder = new TextDecoder('utf-8', { fatal: true });
        const assets = objectValue(JSON.parse(decoder.decode(await boundedBytes(response, 6 * 1024 * 1024))));
        if (!Array.isArray(assets.styles) || assets.styles.length !== entry.manifest.frontend!.styles.length)
          throw new Error('extension_frontend_styles_invalid');
        return { bytes: assetBytes(assets.entry, 2 * 1024 * 1024),
          styles: assets.styles.map(style => decoder.decode(assetBytes(style, 128 * 1024))) };
      });
      signal.throwIfAborted();
      url = URL.createObjectURL(new Blob([bytes as BlobPart], { type: 'text/javascript' }));
      const module = await this.profile(id, 'import', () => import(/* @vite-ignore */ url!)) as BrowserModule;
      signal.throwIfAborted();
      if (typeof module.createFrontend !== 'function') throw new Error('extension_frontend_factory_missing');
      await this.profile(id, 'identity-before', () => this.requireCurrent(entry, signal));
      const transport = new InstalledTransport(kind, id, entry.activationId, this.client, this.media, signal);
      const extension = module.createFrontend(transport);
      const scope = await this.profile(id, 'activate', () => abortable(this.registry.activate(entry.manifest, {
        ...extension,
        activate(context) {
          for (const style of styles) context.registerStyle(style);
          return extension.activate(context);
        }
      }, signal), signal));
      try { await this.profile(id, 'identity-after', () => this.requireCurrent(entry, signal)); signal.throwIfAborted(); }
      catch (error) { await scope.dispose(); throw error; }
      this.active.set(id, { activationId: entry.activationId, controller, scope, transport });
    } catch (error) {
      controller.abort();
      throw error;
    } finally { this.pending.delete(controller); if (url) URL.revokeObjectURL(url); }
  }

  private async profile<T>(id: string, stage: string, action: () => Promise<T>): Promise<T> {
    const name = `nexus.frontend.${id}.${stage}`, start = performance.now();
    try { return await action(); }
    finally {
      performance.clearMeasures(name);
      performance.measure(name, { start, end: performance.now() });
    }
  }

  private async requireCurrent(entry: InstalledModule, signal: AbortSignal): Promise<void> {
    const current = await this.client.read(
      `/control/v1/extensions/${entry.manifest.extensionId}/frontend/${entry.activationId}/identity`, objectValue, signal);
    if (current.activationId !== entry.activationId) throw new Error('extension_frontend_activation_changed');
  }
}

function assetBytes(value: unknown, maximum: number): Uint8Array {
  const encoded = stringValue(value);
  if (encoded.length > Math.ceil(maximum / 3) * 4) throw new Error('extension_frontend_asset_limit');
  const decoded = atob(encoded);
  if (!decoded.length || decoded.length > maximum || btoa(decoded) !== encoded)
    throw new Error('extension_frontend_asset_invalid');
  return Uint8Array.from(decoded, character => character.charCodeAt(0));
}

function readModule(value: unknown): InstalledModule {
  const row = objectValue(value), manifest = objectValue(row.manifest);
  const activationId = stringValue(row.activationId), id = stringValue(manifest.extensionId);
  const frontend = objectValue(manifest.frontend);
  if (!/^[a-z][a-z0-9-]{0,79}$/.test(id) ||
    !/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/.test(activationId) ||
    manifest.format !== 'nexus.extension.manifest' || manifest.version !== 1 || manifest.kind !== 'managed-code' ||
    manifest.sdkVersion !== sdkVersion || manifest.contractFingerprint !== contractFingerprint ||
    frontend.sdkVersion !== sdkVersion || !Array.isArray(frontend.styles) || frontend.styles.length > 16 ||
    !Array.isArray(manifest.capabilities) || !manifest.capabilities.includes('ui.contribute'))
    throw new Error('extension_frontend_identity_invalid');
  return { activationId, manifest: manifest as unknown as ExtensionManifest };
}
