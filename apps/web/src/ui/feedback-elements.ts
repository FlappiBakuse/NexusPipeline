import { NexusElement, elementAttributes } from './element-base.js';
import type { UiElement } from '@nexuspipeline/extension-sdk';
import { iconPaths } from './icon-paths.js';


const tones = `:host{color:var(--nx-color-text,#e9edf5)}:host([data-tone=info]){--tone:var(--nx-color-accent,#80a7ff)}
:host([data-tone=success]){--tone:var(--success,#6bcf9b)}:host([data-tone=warning]){--tone:var(--warning,#e9b66b)}
:host([data-tone=danger]){--tone:var(--nx-color-danger,#ef7070)}`;

export class NexusBadgeElement extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-badge');
  constructor() {
    super(
      'nexus-badge',
      tones +
        `:host{display:inline-flex;align-items:center;min-width:0;max-width:100%;height:24px;
    padding:2px 9px;border:1px solid var(--content-control-border,var(--nx-color-border));border-radius:6px;
    background:var(--muted-soft,var(--content-card-soft));color:var(--nx-color-muted);font-size:12px;font-weight:650;line-height:18px;white-space:nowrap}
    :host([data-tone=info]){background:var(--accent-soft);color:var(--nx-color-accent)}
    :host([data-tone=success]){background:var(--ok-soft);color:var(--success)}
    :host([data-tone=warning]){background:var(--warn-soft);color:var(--warning)}
    :host([data-tone=danger]){background:var(--bad-soft);color:var(--nx-color-danger)}span,slot{min-width:0;overflow:hidden;text-overflow:ellipsis}`,
      '<slot><span></span></slot>'
    );
  }
  protected connect(_signal: AbortSignal): void {}
  protected render(): void {
    this.dataset.tone = String(this.values.tone);
    this.root.querySelector('span')!.textContent = String(this.values.label);
  }
}

export class OverflowText extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-overflow-text');
  private readonly contentSpan: HTMLElement;
  private readonly tooltip: UiElement<'nexus-tooltip'>;
  private observer: ResizeObserver | null = null;
  constructor() {
    super(
      'nexus-overflow-text',
      `:host{display:inline-block;min-width:0;max-width:100%;overflow:hidden;white-space:nowrap;text-overflow:ellipsis}
    span{display:inline-block;max-width:100%;overflow:hidden;text-overflow:ellipsis;vertical-align:bottom}
    :host([data-overflow]):is(:hover,:focus) span{max-width:none;animation:scroll var(--duration,5s) .6s ease-in-out infinite alternate}
    @keyframes scroll{to{transform:translateX(var(--distance,0px))}}@media(prefers-reduced-motion:reduce){span{animation:none!important}}`,
      '<span><slot><span class="fallback"></span></slot></span>'
    );
    this.contentSpan = this.root.querySelector('span')!;
    this.tooltip = document.createElement('nexus-tooltip') as UiElement<'nexus-tooltip'>;
    this.tooltip.style.cssText = 'position:absolute;width:0;height:0';
    this.tooltip.anchor = this;
    this.root.append(this.tooltip);
  }
  protected connect(signal: AbortSignal): void {
    if (typeof ResizeObserver !== 'undefined') {
      this.observer = new ResizeObserver(() => this.measure());
      this.observer.observe(this);
    }
    signal.addEventListener(
      'abort',
      () => {
        this.observer?.disconnect();
        this.observer = null;
      },
      { once: true }
    );
  }
  protected render(): void {
    this.root.querySelector('.fallback')!.textContent = String(this.values.text);
    queueMicrotask(() => {
      if (this.isConnected) this.measure();
    });
  }
  private measure(): void {
    const distance = Math.max(0, this.contentSpan.scrollWidth - this.clientWidth);
    this.toggleAttribute('data-overflow', distance > 1);
    this.tooltip.content = distance > 1 ? String(this.values.text) : '';
    if (distance <= 1) this.tooltip.open = false;
    this.tabIndex = distance > 1 ? 0 : -1;
    this.style.setProperty('--distance', -distance + 'px');
    this.style.setProperty('--duration', Math.max(3, distance / 28) + 's');
  }
}

export class NexusSpinnerElement extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-spinner');
  constructor() {
    super(
      'nexus-spinner',
      `:host{display:inline-flex;align-items:center;gap:8px;color:var(--nx-color-muted,#9eacc1)}
    .ring{width:20px;height:20px;border:2px solid var(--nx-color-border,#394152);border-top-color:var(--nx-color-accent,#80a7ff);
    border-radius:50%;animation:spin .7s linear infinite}:host([data-size=small]) .ring{width:14px;height:14px}
    @keyframes spin{to{transform:rotate(360deg)}}@media(prefers-reduced-motion:reduce){.ring{animation:none}}`,
      '<span class="ring" aria-hidden="true"></span><span></span>'
    );
  }
  protected connect(_signal: AbortSignal): void {}
  protected render(): void {
    this.dataset.size = String(this.values.size);
    this.setAttribute('role', 'status');
    this.root.querySelector('span:last-child')!.textContent = String(this.values.label);
  }
}

export class EmptyState extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-empty-state');
  constructor() {
    super(
      'nexus-empty-state',
      tones +
        `:host{display:grid;justify-items:center;align-content:center;gap:5px;padding:48px 20px;text-align:center;border:1px solid var(--content-card-border,var(--nx-color-border));border-radius:var(--nx-radius-lg);background:var(--content-card,var(--nx-color-surface))}
    h3{margin:0;font-size:15px;color:var(--tone,var(--nx-color-text,#e9edf5))}p{margin:0;font-size:13px;color:var(--nx-color-muted,#9eacc1);overflow-wrap:anywhere}`,
      '<slot name="icon"></slot><h3></h3><p></p><slot name="actions"></slot>'
    );
  }
  protected connect(_signal: AbortSignal): void {}
  protected render(): void {
    this.dataset.tone = String(this.values.tone);
    this.root.querySelector('h3')!.textContent = String(this.values.title);
    this.root.querySelector('p')!.textContent = String(this.values.description);
  }
}

export { NexusTooltipElement } from './tooltip-element.js';

export { NexusToastElement } from './toast-element.js';

export class NexusIconElement extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-icon');
  constructor() {
    super(
      'nexus-icon',
      ':host{display:inline-flex;width:1.25em;height:1.25em;align-items:center;justify-content:center}svg{width:100%;height:100%}',
      '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" focusable="false"></svg>'
    );
  }
  protected connect(_signal: AbortSignal): void {}
  protected render(): void {
    const name = String(this.values.name);
    this.root.querySelector('svg')!.innerHTML = iconPaths[name] ?? iconPaths.dashboard!;
    this.toggleAttribute('aria-hidden', Boolean(this.values.decorative));
    if (!this.values.decorative) this.setAttribute('role', 'img');
    else this.removeAttribute('role');
    this.setAttribute('aria-label', String(this.values.title || name));
  }
}

export { ScrollArea } from './scroll-area-element.js';

export class LoadingState extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-loading-state');
  constructor() {
    super(
      'nexus-loading-state',
      `:host{display:grid;justify-items:center;align-content:center;gap:8px;min-height:176px;padding:32px 24px;color:var(--nx-color-text)}
    .bar{width:min(280px,100%);height:6px;border-radius:99px;background:var(--content-card-soft);overflow:hidden}
    .fill{height:100%;border-radius:inherit;background:var(--nx-color-accent);transition:width .2s ease}
    :host([data-indeterminate]) .fill{animation:loading 1.4s ease-in-out infinite}:host([data-paused]) .fill{animation-play-state:paused}
    @keyframes loading{0%{transform:translateX(-120%)}50%{transform:translateX(100%)}100%{transform:translateX(240%)}}
    @media(prefers-reduced-motion:reduce){.fill{animation:none!important}:host([data-indeterminate]) .fill{transform:translateX(-120%)}}`,
      '<div class="bar" role="progressbar" aria-valuemin="0" aria-valuemax="100"><div class="fill"></div></div><strong></strong>'
    );
  }
  protected connect(signal: AbortSignal): void {
    const visibility = () => this.toggleAttribute('data-paused', document.hidden);
    document.addEventListener('visibilitychange', visibility, { signal });
    visibility();
  }
  protected render(): void {
    this.setAttribute('role', 'status');
    this.root.querySelector('strong')!.textContent = String(this.values.label);
    const progress =
      this.values.progress === null ? null : Math.max(0, Math.min(100, Number(this.values.progress)));
    const bar = this.root.querySelector<HTMLElement>('.bar')!;
    this.toggleAttribute('data-indeterminate', progress === null);
    bar.querySelector<HTMLElement>('.fill')!.style.width = (progress ?? 42) + '%';
    if (progress === null) bar.removeAttribute('aria-valuenow');
    else bar.setAttribute('aria-valuenow', String(progress));
    bar.setAttribute('aria-label', String(this.values.label));
  }
}
