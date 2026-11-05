import type { Cleanup, ExtensionScope } from './extension-scope.js';

export const surfaceIds = Object.freeze([
  'overview.cards', 'overview.after-running',
  'automation.users.badges', 'automation.binding.editor', 'automation.global.editor',
  'automation.scripts.badges', 'automation.script.editor',
  'automation.queues.badges', 'automation.queue.editor',
  'execution.cards', 'execution.running.badges', 'execution.running.preview', 'execution.run.details',
  'records.list.badges', 'records.detail.sections',
  'settings.extra', 'settings.panels', 'application.navigation', 'application.appearance',
] as const);

export type SurfaceId = typeof surfaceIds[number];

export interface SurfaceContext {
  readonly surfaceId: SurfaceId;
  readonly instanceId: string;
  readonly entityId?: string;
  readonly runId?: string;
  readonly attemptId?: string;
}

export interface ExtensionSurface {
  readonly element: HTMLElement;
  readonly context: SurfaceContext;
  readonly scope: ExtensionScope;
  readonly signal: AbortSignal;
  readonly visible: boolean;
  onVisibilityChanged(listener: (visible: boolean) => void): Cleanup;
  setAppearance(overrides: AppearanceOverrides | null): void;
}

export interface AppearanceOverrides {
  readonly accent?: string;
  readonly accentStrong?: string;
  readonly cardDark?: string;
  readonly cardLight?: string;
  readonly cardOpacity?: number;
  readonly secondaryTransparent?: boolean;
}

export interface SurfaceContribution {
  readonly id: string;
  readonly target: SurfaceId;
  mount(surface: ExtensionSurface): void | Cleanup | Promise<void | Cleanup>;
}

export interface RouteContribution {
  readonly id: string;
  readonly path: string;
  readonly label: string;
  mount(surface: ExtensionSurface): void | Cleanup | Promise<void | Cleanup>;
}

export interface FrontendExtensionContext {
  readonly extensionId: string;
  readonly scope: ExtensionScope;
  readonly locale: string;
  readonly appearance: 'light' | 'dark';
  onLocaleChanged(listener: (locale: string) => void): Cleanup;
  registerSurface(contribution: SurfaceContribution): void;
  registerRoute(contribution: RouteContribution): void;
  registerStyle(css: string): void;
  translate(key: string): string;
}

export interface FrontendExtension {
  readonly extensionId: string;
  readonly sdkVersion: '0.1.0';
  readonly contractFingerprint: string;
  activate(context: FrontendExtensionContext): void | Cleanup | Promise<void | Cleanup>;
}
