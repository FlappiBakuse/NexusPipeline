import {
  contractFingerprint, sdkVersion, type ExtensionManifest, type FrontendExtension,
  type FrontendTransport, type ExtensionScope
} from '@nexuspipeline/extension-sdk';
import { ControlClient, arrayValue, boundedBytes, objectValue, stringValue } from '../control/client';
import type { MediaPool } from '../control/media-pool';
import type { FrontendRegistry } from './frontend-registry';
import { InstalledTransport } from './frontend-transport';

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

  constructor(private readonly client: ControlClient, private readonly media: MediaPool,
    private readonly registry: FrontendRegistry, private readonly report: (error: unknown) => void) {}

  synchronize(): Promise<void> {
    if (this.connection.signal.aborted) this.connection = new AbortController();
    this.changed = true;
    if (!this.work) this.work = this.drain().finally(() => {
      this.work = undefined;
      if (this.changed && !this.connection.signal.aborted && !this.lifetime.signal.aborted)
        queueMicrotask(() => { void this.synchronize().catch(this.report); });
    });
    return this.work;
  }

  disconnect(): void {
    this.changed = false;
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
      const signal = AbortSignal.any([this.lifetime.signal, this.connection.signal]);
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
      for (const entry of entries) {
        if (this.active.has(entry.manifest.extensionId)) continue;
        try { await this.load(entry, kind, signal); }
        catch (error) { if (!signal.aborted) this.report(error); }
      }
    }
  }

  private async load(entry: InstalledModule, kind: 'local' | 'remote', connection: AbortSignal): Promise<void> {
    const id = entry.manifest.extensionId;
    const controller = new AbortController();
    const signal = AbortSignal.any([controller.signal, this.lifetime.signal, connection]);
    const base = `/control/v1/extensions/${id}/frontend/${entry.activationId}/assets/`;
    let url: string | undefined;
    try {
      const response = await this.client.response(base + 'entry', signal);
      const bytes = await boundedBytes(response, 2 * 1024 * 1024);
      const styles: string[] = [];
      for (let index = 0; index < entry.manifest.frontend!.styles.length; index++) {
        const style = await this.client.response(base + index, signal);
        styles.push(new TextDecoder('utf-8', { fatal: true }).decode(await boundedBytes(style, 128 * 1024)));
      }
      signal.throwIfAborted();
      url = URL.createObjectURL(new Blob([bytes as BlobPart], { type: 'text/javascript' }));
      const module = await import(/* @vite-ignore */ url) as BrowserModule;
      signal.throwIfAborted();
      if (typeof module.createFrontend !== 'function') throw new Error('extension_frontend_factory_missing');
      await this.requireCurrent(entry, signal);
      const transport = new InstalledTransport(kind, id, entry.activationId, this.client, this.media, signal);
      const extension = module.createFrontend(transport);
      const scope = await this.registry.activate(entry.manifest, {
        ...extension,
        activate(context) {
          for (const style of styles) context.registerStyle(style);
          return extension.activate(context);
        }
      }, signal);
      try { await this.requireCurrent(entry, signal); signal.throwIfAborted(); }
      catch (error) { await scope.dispose(); throw error; }
      this.active.set(id, { activationId: entry.activationId, controller, scope, transport });
    } catch (error) {
      controller.abort();
      throw error;
    } finally { if (url) URL.revokeObjectURL(url); }
  }

  private async requireCurrent(entry: InstalledModule, signal: AbortSignal): Promise<void> {
    const current = await this.client.read(
      `/control/v1/extensions/${entry.manifest.extensionId}/frontend/${entry.activationId}/identity`, objectValue, signal);
    if (current.activationId !== entry.activationId) throw new Error('extension_frontend_activation_changed');
  }
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
