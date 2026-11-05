import { NexusElement, elementAttributes } from './element-base.js';
import { ScrollRails, scrollRailStyles, scrollRailMarkup } from './scroll-rails.js';

const styles =
  scrollRailStyles +
  `
:host {
  position: relative;
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}
:host([hidden]) { display: none; }
.viewport {
  width: 100%;
  flex: 1 1 auto;
  height: auto;
  min-width: 0;
  min-height: 0;
  max-height: inherit;
  overflow: auto;
  overscroll-behavior: contain;
  scrollbar-width: none;
  outline: none;
}
.viewport::-webkit-scrollbar { display: none; }
.inside { min-width: 100%; min-height: 100%; }

`;

export class ScrollArea extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-scroll-area');
  private readonly viewport: HTMLElement;
  private readonly inside: HTMLElement;
  private readonly rails: ScrollRails;
  constructor() {
    super(
      'nexus-scroll-area',
      styles,
      `
      <div class="viewport" tabindex="0"><div class="inside"><slot></slot></div></div>
      ${scrollRailMarkup}`
    );
    this.viewport = this.root.querySelector('.viewport')!;
    this.inside = this.root.querySelector('.inside')!;
    this.rails = new ScrollRails(this, this.root, this.viewport, this.inside, () => this.values.direction);
    Object.defineProperties(this, {
      scrollTop: {
        get: () => this.viewport.scrollTop,
        set: (value) => {
          this.viewport.scrollTop = value;
        }
      },
      scrollLeft: {
        get: () => this.viewport.scrollLeft,
        set: (value) => {
          this.viewport.scrollLeft = value;
        }
      },
      scrollHeight: { get: () => this.viewport.scrollHeight },
      scrollWidth: { get: () => this.viewport.scrollWidth }
    });
  }

  protected connect(signal: AbortSignal): void {
    this.rails.connect(signal);
    this.root.querySelector('slot')!.addEventListener('slotchange', () => this.rails.schedule(), { signal });
    this.viewport.addEventListener(
      'scroll',
      (event) => {
        if (!event.composed) this.dispatchEvent(new Event('scroll', { composed: true }));
      },
      { signal, passive: true }
    );
  }

  override focus(options?: FocusOptions): void {
    this.viewport.focus(options);
  }
  override scrollTo(options?: ScrollToOptions): void;
  override scrollTo(x: number, y: number): void;
  override scrollTo(value?: ScrollToOptions | number, y?: number): void {
    if (typeof value === 'number') this.viewport.scrollTo(value, y ?? 0);
    else this.viewport.scrollTo(value);
  }
  override scrollBy(options?: ScrollToOptions): void;
  override scrollBy(x: number, y: number): void;
  override scrollBy(value?: ScrollToOptions | number, y?: number): void {
    if (typeof value === 'number') this.viewport.scrollBy(value, y ?? 0);
    else this.viewport.scrollBy(value);
  }

  protected render(): void {
    const direction = this.values.direction;
    this.viewport.style.overflowX = direction === 'vertical' ? 'hidden' : 'auto';
    this.viewport.style.overflowY = direction === 'horizontal' ? 'hidden' : 'auto';
    this.viewport.setAttribute('aria-label', String(this.values.label));
    this.viewport.setAttribute('role', 'region');
    this.rails.schedule();
  }
}
