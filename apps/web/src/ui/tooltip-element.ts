import { elementAttributes } from './element-base.js';
import { FloatingElement } from './floating-element.js';

export class NexusTooltipElement extends FloatingElement {
  static readonly observedAttributes = [...elementAttributes('nexus-tooltip'), 'hidden'];
  private timer: number | null = null;
  private anchorOwner: AbortController | undefined;
  private anchorNode: HTMLElement | undefined;

  constructor() {
    super(
      'nexus-tooltip',
      `
      :host { display: inline-flex; }
      :host([hidden]) { display: none; }
      .panel {
        position: fixed;
        inset: auto;
        margin: 0;
        max-width: min(240px, calc(100vw - 16px));
        padding: 8px 12px;
        border: 1px solid var(--nx-color-border, #394152);
        border-radius: 8px;
        background: var(--nx-color-surface, #1e2330);
        color: var(--nx-color-text, #e9edf5);
        font: inherit;
        font-size: 12px;
        line-height: 1.55;
        box-shadow: 0 12px 28px #0005;
        pointer-events: none;
        overflow-wrap: anywhere;
      }
      .panel[hidden] { display: none; }
      .panel::backdrop { display: none; pointer-events: none; }
    `,
      '<slot></slot><span class="panel" role="tooltip" popover="manual" hidden></span>',
      '.panel'
    );
  }

  protected connect(signal: AbortSignal): void {
    this.attachFloat(signal);
    signal.addEventListener(
      'abort',
      () => {
        this.anchorOwner?.abort();
        this.anchorOwner = undefined;
        this.anchorNode = undefined;
        this.clearTimer();
        if (this.panel.matches(':popover-open')) this.panel.hidePopover();
      },
      { once: true }
    );
  }

  protected render(): void {
    if (this.hidden && this.values.open) {
      this.close('owner');
      return;
    }
    if (this.hidden) this.clearTimer();
    this.bindAnchor();
    this.panel.textContent = String(this.values.content);
    this.panel.hidden = !this.values.open;
    if (this.values.open) {
      if (!this.panel.matches(':popover-open')) this.panel.showPopover();
      this.place();
    } else if (this.panel.matches(':popover-open')) this.panel.hidePopover();
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.setProperty('open', false);
  }

  protected override close(reason: 'button' | 'escape' | 'backdrop' | 'owner'): void {
    this.clearTimer();
    if (!this.values.open) return;
    this.setProperty('open', false);
    super.close(reason);
  }

  private bindAnchor(): void {
    const anchor = this.values.anchor instanceof HTMLElement ? this.values.anchor : this;
    if (anchor === this.anchorNode) return;
    this.anchorOwner?.abort();
    this.clearTimer();
    this.anchorNode = anchor;
    this.anchorOwner = new AbortController();
    const signal = this.anchorOwner.signal;
    anchor.addEventListener('pointerenter', () => this.schedule(), { signal });
    anchor.addEventListener('pointerleave', () => this.close('owner'), { signal });
    anchor.addEventListener('focusin', () => this.schedule(), { signal });
    anchor.addEventListener('focusout', () => this.close('owner'), { signal });
  }

  private schedule(): void {
    if (this.values.open || this.timer !== null || !this.values.content) return;
    this.timer = window.setTimeout(
      () => {
        this.timer = null;
        if (this.isConnected && !this.hidden && this.anchorNode?.isConnected) this.setProperty('open', true);
      },
      Math.max(0, Number(this.values.delay))
    );
  }

  private clearTimer(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }
}
