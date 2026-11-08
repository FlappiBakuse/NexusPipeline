import {
  ExtensionScope,
  contractFingerprint,
  sdkVersion,
  surfaceIds,
  type ExtensionManifest,
  type FrontendExtension,
  type FrontendExtensionContext,
  type RouteContribution,
  type SurfaceContribution,
  type SurfaceContext,
  type SurfaceId
} from '@nexuspipeline/extension-sdk';
import { abortable } from '../control/abortable';
import { SurfaceSession } from './surface-session.js';

interface ActiveExtension {
  readonly scope: ExtensionScope;
  readonly surfaces: readonly SurfaceContribution[];
  readonly routes: readonly RouteContribution[];
  readonly localeListeners: Set<(locale: string) => void>;
}

export interface FrontendEnvironment {
  readonly locale: string;
  readonly appearance: 'light' | 'dark';
  translate(extensionId: string, key: string): string;
}

export class FrontendRegistry {
  private readonly active = new Map<string, ActiveExtension>();
  private readonly pending = new Map<string, ExtensionScope>();
  preparationSignal: AbortSignal | undefined;
  preparationFailure: ((error: unknown) => void) | undefined;
  private readonly retries = new Set<() => Promise<void>>();
  subscribeRetry(retry: () => Promise<void>): () => void {
    this.retries.add(retry);
    return () => { this.retries.delete(retry); };
  }
  retryMounts(): void { for (const retry of this.retries) this.trackMount(retry()); }
  reportMountFailure(error: unknown): void { this.preparationFailure?.(error); this.report(error); }
  private readonly mounting = new Set<Promise<void>>();
  trackMount(work: Promise<void>): void {
    this.mounting.add(work);
    void work.finally(() => this.mounting.delete(work)).catch(error => this.report(error));
  }
  async settled(signal: AbortSignal): Promise<void> {
    while (this.mounting.size) await abortable(Promise.allSettled([...this.mounting]).then(() => {}), signal);
  }
  private readonly listeners = new Set<(extensionId: string) => void>();

  constructor(private readonly environment: FrontendEnvironment) {}

  refreshLocale(): void {
    for (const owner of this.active.values()) {
      for (const listener of owner.localeListeners) {
        try { listener(this.environment.locale); }
        catch (error) { this.report(error); }
      }
    }
    this.changed('');
  }

  subscribe(listener: (extensionId: string) => void): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  private changed(extensionId: string): void {
    for (const listener of this.listeners) listener(extensionId);
  }

  async activate(
    manifest: ExtensionManifest,
    extension: FrontendExtension,
    signal?: AbortSignal
  ): Promise<ExtensionScope> {
    this.validate(manifest, extension);
    const id = manifest.extensionId;
    if (this.active.has(id) || this.pending.has(id))
      throw new Error('Extension already owns frontend registrations');
    const scope = new ExtensionScope();
    this.pending.set(id, scope);
    const surfaces: SurfaceContribution[] = [];
    const routes: RouteContribution[] = [];
    const styles: HTMLStyleElement[] = [];
    const localeListeners = new Set<(locale: string) => void>();
    scope.signal.addEventListener('abort', () => localeListeners.clear(), { once: true });
    let accepting = true;
    const guard = (): void => {
      if (!accepting || scope.signal.aborted) throw new Error('Extension registration is closed');
    };
    const cancel = (): void => {
      accepting = false;
      if (this.pending.get(id) === scope) this.pending.delete(id);
      void scope.dispose().catch((error) => this.report(error));
    };
    signal?.addEventListener('abort', cancel, { once: true });
    if (signal?.aborted) cancel();
    const environment = this.environment;
    const context: FrontendExtensionContext = Object.freeze({
      extensionId: id,
      scope,
      get locale() { return environment.locale; },
      appearance: this.environment.appearance,
      onLocaleChanged: (listener: (locale: string) => void) => {
        if (scope.signal.aborted) return () => {};
        localeListeners.add(listener);
        return () => { localeListeners.delete(listener); };
      },
      translate: (key: string) => this.environment.translate(id, key),
      registerSurface: (contribution: SurfaceContribution) => {
        guard();
        this.validateContribution(
          contribution.id,
          surfaces.map((item) => item.id)
        );
        if (!surfaceIds.includes(contribution.target) || typeof contribution.mount !== 'function') {
          throw new TypeError('Unknown surface or renderer');
        }
        const card = contribution.dashboard;
        if (card && (contribution.target !== 'overview.cards' ||
          !/^[a-z][a-z0-9.-]{0,95}$/.test(card.key) ||
          ['status', 'running', 'history-duration', 'history-counts'].includes(card.key) ||
          !card.title.trim() || card.title.length > 120 || card.description.length > 1024 ||
          typeof card.defaultVisible !== 'boolean' || !Number.isSafeInteger(card.order)))
          throw new TypeError('Invalid dashboard contribution');
        surfaces.push(
          Object.freeze({
            id: contribution.id,
            target: contribution.target,
            ...(card ? { dashboard: Object.freeze({ ...card, get title() { return card.title; }, get description() { return card.description; } }) } : {}),
            mount: contribution.mount.bind(contribution)
          })
        );
      },
      registerRoute: (contribution: RouteContribution) => {
        guard();
        this.validateContribution(
          contribution.id,
          routes.map((item) => item.id)
        );
        if (
          !/^\/plugin\/[a-z0-9-]+(?:\/[a-z0-9-]+)*$/.test(contribution.path) ||
          !contribution.label.trim()
        ) {
          throw new TypeError('Invalid extension route');
        }
        routes.push(Object.freeze({
          id: contribution.id, path: contribution.path,
          get label() { return contribution.label; },
          mount: contribution.mount.bind(contribution)
        }));
      },
      registerStyle: (css: string) => {
        guard();
        if (typeof css !== 'string' || css.length > 128 * 1024 || styles.length >= 16)
          throw new TypeError('Extension style is too large');
        const style = document.createElement('style');
        style.dataset.extensionId = id;
        style.textContent = css;
        styles.push(style);
      }
    });
    try {
      scope.signal.throwIfAborted();
      const cleanup = await extension.activate(context);
      if (cleanup) await scope.own(cleanup);
      accepting = false;
      scope.signal.throwIfAborted();
      this.commit(id, scope, surfaces, routes, styles, localeListeners);
      return scope;
    } catch (error) {
      accepting = false;
      try {
        await scope.dispose();
      } catch (cleanupError) {
        throw new AggregateError([error, cleanupError], 'Frontend activation and rollback failed');
      }
      throw error;
    } finally {
      if (this.pending.get(id) === scope) this.pending.delete(id);
      signal?.removeEventListener('abort', cancel);
    }
  }

  listSurfaces(target: SurfaceId): readonly { extensionId: string; id: string; dashboard?: SurfaceContribution['dashboard'] }[] {
    return Object.freeze(
      [...this.active].flatMap(([extensionId, owner]) =>
        owner.surfaces
          .filter((item) => item.target === target)
          .map((item) => Object.freeze({ extensionId, id: item.id, dashboard: item.dashboard }))
      )
    );
  }

  listRoutes(): readonly { extensionId: string; id: string; path: string; label: string }[] {
    return Object.freeze(
      [...this.active].flatMap(([extensionId, owner]) =>
        owner.routes.map((item) =>
          Object.freeze({ extensionId, id: item.id, path: item.path, label: item.label })
        )
      )
    );
  }

  async mount(
    extensionId: string,
    id: string,
    element: HTMLElement,
    context: SurfaceContext,
    signal?: AbortSignal
  ): Promise<SurfaceSession> {
    const owner = this.active.get(extensionId);
    const contribution =
      owner?.surfaces.find((item) => item.id === id && item.target === context.surfaceId) ??
      owner?.routes.find((item) => item.id === id && context.surfaceId === 'application.navigation');
    if (!owner || !contribution || owner.scope.signal.aborted) throw new Error('Surface is not active');
    const scope = await owner.scope.child();
    scope.signal.throwIfAborted();
    const candidate = document.createElement('div');
    const session = new SurfaceSession(candidate, context, scope);
    const signals = [this.preparationSignal, signal].filter((value): value is AbortSignal => !!value);
    const preparing = signals.length ? AbortSignal.any(signals) : undefined;
    const cancel = () => { void scope.dispose().catch(error => this.report(error)); };
    preparing?.addEventListener('abort', cancel, { once: true });
    if (preparing?.aborted) cancel();
    try {
      const work = Promise.resolve().then(() => contribution.mount(session)).then(async cleanup => {
        if (cleanup) await scope.own(cleanup);
      });
      await abortable(work, scope.signal);
      scope.signal.throwIfAborted();
      element.append(candidate);
      return session;
    } catch (error) {
      try {
        await scope.dispose();
      } catch (cleanupError) {
        throw new AggregateError([error, cleanupError], 'Surface mount and cleanup failed');
      }
      throw error;
    } finally { preparing?.removeEventListener('abort', cancel); }
  }

  private commit(
    id: string,
    scope: ExtensionScope,
    surfaces: SurfaceContribution[],
    routes: RouteContribution[],
    styles: HTMLStyleElement[],
    localeListeners: Set<(locale: string) => void>
  ): void {
    const paths = new Set(this.listRoutes().map((route) => route.path));
    const cards = new Set(this.listSurfaces('overview.cards').flatMap(item => item.dashboard ? [item.dashboard.key] : []));
    for (const surface of surfaces) {
      if (!surface.dashboard) continue;
      if (cards.has(surface.dashboard.key)) throw new Error('Dashboard key already registered');
      cards.add(surface.dashboard.key);
    }
    for (const route of routes) {
      if (paths.has(route.path)) throw new Error('Route already registered');
      paths.add(route.path);
    }
    scope.signal.addEventListener(
      'abort',
      () => {
        this.active.delete(id);
        for (const style of styles) style.remove();
        this.changed(id);
      },
      { once: true }
    );
    for (const style of styles) document.head.append(style);
    this.active.set(id, { scope, surfaces: Object.freeze(surfaces), routes: Object.freeze(routes), localeListeners });
    this.changed(id);
  }

  private validate(manifest: ExtensionManifest, extension: FrontendExtension): void {
    if (
      manifest.format !== 'nexus.extension.manifest' ||
      manifest.version !== 1 ||
      manifest.kind !== 'managed-code' ||
      !manifest.frontend ||
      manifest.frontend.sdkVersion !== sdkVersion ||
      !manifest.capabilities.includes('ui.contribute') ||
      manifest.sdkVersion !== sdkVersion ||
      manifest.contractFingerprint !== contractFingerprint ||
      extension.sdkVersion !== sdkVersion ||
      extension.contractFingerprint !== contractFingerprint ||
      extension.extensionId !== manifest.extensionId ||
      typeof extension.activate !== 'function'
    )
      throw new TypeError('Unsupported frontend identity or capability');
  }

  private validateContribution(id: string, registered: readonly string[]): void {
    if (!/^[a-z][a-z0-9.-]{0,95}$/.test(id) || registered.includes(id) || registered.length >= 64)
      throw new TypeError('Invalid or duplicate contribution id');
  }

  private report(error: unknown): void {
    document.dispatchEvent(new CustomEvent('nexus-extension-error', { detail: error }));
  }
}
