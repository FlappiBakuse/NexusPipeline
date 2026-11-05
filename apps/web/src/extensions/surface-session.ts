import {
  ExtensionScope,
  type ExtensionSurface,
  type SurfaceContext,
  type Cleanup,
  type AppearanceOverrides
} from '@nexuspipeline/extension-sdk';
import { releaseAppearance, setAppearance } from './appearance-owner.js';

export class SurfaceSession implements ExtensionSurface {
  readonly signal: AbortSignal;
  readonly context: SurfaceContext;
  private readonly listeners = new Set<(visible: boolean) => void>();
  private isVisible = false;

  constructor(
    readonly element: HTMLElement,
    context: SurfaceContext,
    readonly scope: ExtensionScope
  ) {
    this.context = Object.freeze({ ...context });
    this.signal = scope.signal;
    this.signal.addEventListener(
      'abort',
      () => {
        releaseAppearance(this);
        this.setVisible(false);
        this.listeners.clear();
        this.element.remove();
        this.element.replaceChildren();
      },
      { once: true }
    );
  }

  get visible(): boolean {
    return this.isVisible;
  }

  setAppearance(overrides: AppearanceOverrides | null): void {
    setAppearance(this, overrides);
  }

  onVisibilityChanged(listener: (visible: boolean) => void): Cleanup {
    if (this.signal.aborted) return () => {};
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  setVisible(visible: boolean): void {
    const next = visible && !this.signal.aborted && this.element.isConnected;
    if (next === this.isVisible) return;
    this.isVisible = next;
    const errors: unknown[] = [];
    for (const listener of this.listeners) {
      try {
        listener(next);
      } catch (error) {
        errors.push(error);
      }
    }
    if (errors.length)
      this.element.dispatchEvent(
        new CustomEvent('nexus-surface-error', {
          detail: new AggregateError(errors, 'Surface visibility listeners failed')
        })
      );
  }
}
