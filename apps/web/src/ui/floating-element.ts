import { NexusElement } from './element-base.js';

export abstract class FloatingElement extends NexusElement {
  protected readonly panel: HTMLElement;
  private previousFocus: HTMLElement | null = null;
  private placed = false;

  protected constructor(
    name: ConstructorParameters<typeof NexusElement>[0],
    styles: string,
    markup: string,
    selector: string
  ) {
    super(name, styles, markup);
    this.panel = this.root.querySelector(selector)!;
  }

  protected attachFloat(signal: AbortSignal): void {
    document.addEventListener(
      'pointerdown',
      (event) => {
        if (
          !this.values.open ||
          this.contains(event.target as Node) ||
          event.composedPath().includes(this) ||
          (this.values.anchor instanceof HTMLElement && this.values.anchor.contains(event.target as Node))
        )
          return;
        this.close('backdrop');
      },
      { capture: true, signal }
    );
    document.addEventListener(
      'keydown',
      (event) => {
        if (this.values.open && event.key === 'Escape') {
          event.preventDefault();
          this.close('escape');
        }
      },
      { capture: true, signal }
    );
    window.addEventListener('resize', () => this.place(), { signal });
    window.addEventListener('scroll', () => this.place(), { capture: true, signal });
  }

  protected float(): void {
    this.panel.hidden = !this.values.open;
    if (!this.values.open) {
      if (this.placed && this.previousFocus?.isConnected) this.previousFocus.focus({ preventScroll: true });
      this.placed = false;
      this.previousFocus = null;
      return;
    }
    if (!this.placed) {
      this.previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      this.placed = true;
      queueMicrotask(() => {
        if (this.values.open && this.isConnected) this.panel.focus({ preventScroll: true });
      });
    }
    this.place();
  }

  protected place(): void {
    if (!this.values.open || this.panel.hidden) return;
    const anchor = this.values.anchor instanceof HTMLElement ? this.values.anchor : this;
    const rect = anchor.getBoundingClientRect();
    this.panel.style.position = 'fixed';
    this.panel.style.left = Math.max(8, Math.min(rect.left, innerWidth - this.panel.offsetWidth - 8)) + 'px';
    this.panel.style.top =
      (rect.bottom + this.panel.offsetHeight + 8 > innerHeight && rect.top > this.panel.offsetHeight
        ? rect.top - this.panel.offsetHeight - 4
        : rect.bottom + 4) + 'px';
  }

  protected close(reason: 'button' | 'escape' | 'backdrop' | 'owner'): void {
    this.emit('nexus-close', { reason });
  }
  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.placed = false;
    this.previousFocus = null;
  }
}
