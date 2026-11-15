import { elementAttributes } from './element-base.js';
import { FloatingElement } from './floating-element.js';

let descriptionSequence = 0;

export class NexusTooltipElement extends FloatingElement {
  static readonly observedAttributes = [...elementAttributes('nexus-tooltip'), 'hidden'];
  private static active: NexusTooltipElement | undefined;
  private timer: number | undefined;
  private leaveTimer: number | undefined;
  private frame: number | undefined;
  private anchorOwner: AbortController | undefined;
  private anchorNode: HTMLElement | undefined;
  private description: HTMLElement | undefined;
  private described: HTMLElement | undefined;
  private hovering = false;
  private reading = false;

  constructor() {
    super(
      'nexus-tooltip',
      `:host{display:inline-flex}:host([hidden]){display:none}
      .panel{position:fixed;inset:auto;margin:0;max-width:min(240px,calc(100vw - 16px));
      padding:8px 12px;border:1px solid var(--nx-color-border,#394152);border-radius:8px;
      background:var(--nx-color-surface,#1e2330);color:var(--nx-color-text,#e9edf5);
      font:inherit;font-size:12px;line-height:1.55;box-shadow:0 12px 28px #0005;overflow-wrap:anywhere}
      .panel[hidden]{display:none}.panel::backdrop{display:none;pointer-events:none}`,
      '<slot></slot><span class="panel" role="tooltip" popover="manual" hidden></span>',
      '.panel'
    );
  }

  protected connect(signal: AbortSignal): void {
    this.attachFloat(signal);
    this.panel.addEventListener('pointerenter', () => { this.reading = true; this.cancelLeave(); }, { signal });
    this.panel.addEventListener('pointerleave', () => { this.reading = false; this.leave(); }, { signal });
    window.addEventListener('blur', () => this.close('owner'), { signal });
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) this.close('owner');
    }, { signal });
    signal.addEventListener('abort', () => {
      this.anchorOwner?.abort();
      this.anchorOwner = undefined;
      this.anchorNode = undefined;
      this.releaseDescription();
      this.clearTimer();
      this.cancelLeave();
      this.stopWatching();
      this.hovering = this.reading = false;
      if (NexusTooltipElement.active === this) NexusTooltipElement.active = undefined;
      if (this.panel.matches(':popover-open')) this.panel.hidePopover();
    }, { once: true });
  }

  protected render(): void {
    this.bindAnchor();
    this.describeAnchor();
    if ((this.hidden || !this.values.content) && this.values.open) {
      this.close('owner');
      return;
    }
    if (this.hidden || !this.values.content) this.clearTimer();
    this.panel.textContent = String(this.values.content);
    this.panel.hidden = !this.values.open;
    if (this.values.open) {
      if (NexusTooltipElement.active !== this) {
        NexusTooltipElement.active?.close('owner');
        NexusTooltipElement.active = this;
      }
      if (!this.panel.matches(':popover-open')) this.panel.showPopover();
      this.place();
      this.watchAnchor();
    } else {
      this.stopWatching();
      if (NexusTooltipElement.active === this) NexusTooltipElement.active = undefined;
      if (this.panel.matches(':popover-open')) this.panel.hidePopover();
    }
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.setProperty('open', false);
  }

  protected override close(reason: 'button' | 'escape' | 'backdrop' | 'owner'): void {
    this.clearTimer();
    this.cancelLeave();
    if (!this.values.open) return;
    this.setProperty('open', false);
    super.close(reason);
  }

  private bindAnchor(): void {
    const anchor = this.values.anchor instanceof HTMLElement ? this.values.anchor : this;
    if (anchor === this.anchorNode) return;
    this.anchorOwner?.abort();
    this.releaseDescription();
    this.clearTimer();
    this.cancelLeave();
    this.hovering = this.reading = false;
    this.anchorNode = anchor;
    this.anchorOwner = new AbortController();
    const signal = this.anchorOwner.signal;
    for (const select of anchor.matches('nexus-select') ? [anchor] : anchor.querySelectorAll('nexus-select')) {
      select.shadowRoot?.addEventListener('beforetoggle', event => {
        if ((event as ToggleEvent).newState === 'open') this.close('owner');
      }, { signal, capture: true });
    }
    anchor.addEventListener('pointerenter', () => {
      this.hovering = true;
      this.cancelLeave();
      this.schedule();
    }, { signal });
    anchor.addEventListener('pointerleave', () => { this.hovering = false; this.leave(); }, { signal });
    anchor.addEventListener('focusin', () => this.schedule(), { signal });
    anchor.addEventListener('focusout', () => queueMicrotask(() => this.leave()), { signal });
    queueMicrotask(() => {
      if (!signal.aborted && anchor.matches(':focus-within')) this.schedule();
    });
  }

  private describeAnchor(): void {
    const anchor = this.anchorNode;
    if (!anchor?.parentNode || !this.values.content || this.hidden) {
      this.releaseDescription();
      return;
    }
    const control = anchor.matches('label,.form-number,.form-select')
      ? anchor.querySelector<HTMLElement>('nexus-number-input,nexus-select,nexus-text-input,nexus-path-picker,input,textarea') ?? anchor
      : anchor;
    if (this.described !== control) this.releaseDescription();
    if (!this.description) {
      this.description = document.createElement('span');
      this.description.id = 'nexus-help-' + ++descriptionSequence;
      this.description.hidden = true;
      this.description.style.setProperty('display', 'none', 'important');
      this.described = control;
    }
    // The description shares the anchor's tree; input shadow roots can reference their ancestor tree.
    const tree = control.getRootNode();
    const parent = tree instanceof ShadowRoot ? tree : control.ownerDocument.body;
    if (this.description.parentNode !== parent) parent.append(this.description);
    this.description.textContent = String(this.values.content);
    const ids = (control.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean);
    if (!ids.includes(this.description.id)) control.setAttribute('aria-describedby', [...ids, this.description.id].join(' '));
  }

  private releaseDescription(): void {
    if (this.described && this.description) {
      const ids = (this.described.getAttribute('aria-describedby') || '').split(/\s+/)
        .filter(id => id && id !== this.description!.id);
      if (ids.length) this.described.setAttribute('aria-describedby', ids.join(' '));
      else this.described.removeAttribute('aria-describedby');
    }
    this.description?.remove();
    this.description = this.described = undefined;
  }

  private schedule(): void {
    this.cancelLeave();
    if (this.values.open || this.timer !== undefined || !this.values.content || this.hasExpandedSelect()) return;
    this.timer = window.setTimeout(() => {
      this.timer = undefined;
      if (this.isConnected && !this.hidden && this.visibleAnchor()) this.setProperty('open', true);
    }, Math.max(0, Number(this.values.delay)));
  }

  private leave(): void {
    this.cancelLeave();
    if (this.hovering || this.reading || this.anchorNode?.matches(':focus-within')) return;
    this.clearTimer();
    this.leaveTimer = window.setTimeout(() => this.close('owner'), 100);
  }

  private visibleAnchor(): boolean {
    const anchor = this.anchorNode;
    if (!anchor?.isConnected || !anchor.getClientRects().length || anchor.closest('[inert]') || this.hasExpandedSelect()) return false;
    const rect = anchor.getBoundingClientRect();
    let left = Math.max(0, rect.left), right = Math.min(innerWidth, rect.right);
    let top = Math.max(0, rect.top), bottom = Math.min(innerHeight, rect.bottom);
    let parent: HTMLElement | null = anchor;
    while (parent) {
      const style = getComputedStyle(parent);
      if (parent.inert || style.visibility === 'hidden') return false;
      if (parent !== anchor && style.display !== 'contents') {
        const clip = parent.getBoundingClientRect();
        if (/(hidden|clip|auto|scroll)/.test(style.overflowX)) { left = Math.max(left, clip.left); right = Math.min(right, clip.right); }
        if (/(hidden|clip|auto|scroll)/.test(style.overflowY)) { top = Math.max(top, clip.top); bottom = Math.min(bottom, clip.bottom); }
      }
      if (parent.matches('dialog[open],:popover-open')) break;
      const tree = parent.getRootNode();
      parent = parent.assignedSlot ?? parent.parentElement ?? (tree instanceof ShadowRoot ? tree.host as HTMLElement : null);
    }
    return right > left && bottom > top;
  }

  private hasExpandedSelect(): boolean {
    const anchor = this.anchorNode;
    if (!anchor) return false;
    const selects = anchor.matches('nexus-select') ? [anchor] : [...anchor.querySelectorAll('nexus-select')];
    return selects.some(select => select.shadowRoot?.querySelector('[role=combobox]')?.getAttribute('aria-expanded') === 'true');
  }

  private watchAnchor(): void {
    if (this.frame !== undefined) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = undefined;
      if (!this.visibleAnchor()) { this.close('owner'); return; }
      this.place();
      this.watchAnchor();
    });
  }

  private stopWatching(): void {
    if (this.frame !== undefined) cancelAnimationFrame(this.frame);
    this.frame = undefined;
  }

  private clearTimer(): void {
    if (this.timer !== undefined) clearTimeout(this.timer);
    this.timer = undefined;
  }

  private cancelLeave(): void {
    if (this.leaveTimer !== undefined) clearTimeout(this.leaveTimer);
    this.leaveTimer = undefined;
  }
}
