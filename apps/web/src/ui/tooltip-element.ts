import { elementAttributes } from './element-base.js';
import { FloatingElement } from './floating-element.js';
import { ModalSurface } from './modal-surface.js';

let descriptionSequence = 0;
const fieldControl = 'input:not([type=button],[type=submit],[type=hidden]),textarea,select,nexus-text-input,nexus-text-area,nexus-number-input,nexus-select,nexus-path-picker,nexus-file-picker,nexus-switch,nexus-range,nexus-color-picker,nexus-date-range-picker,nexus-time-picker';

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
  private readonly trigger: HTMLButtonElement;
  private readonly text: HTMLElement;
  private field?: HTMLElement;

  constructor() {
    super(
      'nexus-tooltip',
      `:host{display:inline-flex;vertical-align:middle}:host([hidden]){display:none}
      :host(:empty:not([data-field-help])){position:absolute;width:0;height:0}
      button{position:relative;display:grid;place-content:center;width:20px;height:20px;padding:0;margin-inline:6px 2px;
      border:0;border-radius:50%;background:transparent;color:var(--nx-color-muted);cursor:pointer}
      button::after{content:"";position:absolute;inset:-2px}
      .glyph{display:grid;place-content:center;box-sizing:border-box;width:15px;height:15px;border-radius:50%;
      border:1px solid var(--border-strong,var(--nx-color-border));pointer-events:none}
      .glyph svg{display:block;width:10px;height:10px}
      button:hover,button[aria-expanded=true]{color:var(--accent)}
      button:hover .glyph,button[aria-expanded=true] .glyph{border-color:currentColor}
      button:focus-visible{outline:2px solid var(--accent);outline-offset:2px}
      .panel{position:fixed;inset:auto;margin:0;box-sizing:border-box;overflow:visible;
      max-width:min(320px,calc(100vw - 16px));border:1px solid var(--border-strong,var(--nx-color-border));border-radius:10px;
      background:var(--content-card-secondary,var(--nx-color-surface));color:var(--nx-color-text);
      font:400 13px/1.55 system-ui;box-shadow:0 12px 28px #0005}
      .panel::before{content:"";position:absolute;left:calc(var(--pointer) - 5px);top:-6px;width:10px;height:10px;
      background:inherit;border:inherit;border-right:0;border-bottom:0;transform:rotate(45deg)}
      .panel[data-side=above]::before{top:auto;bottom:-6px;transform:rotate(225deg)}
      .text{display:block;position:relative;box-sizing:border-box;padding:8px 12px;max-height:var(--help-height);
      overflow:auto;overflow-wrap:anywhere;white-space:pre-wrap}
      [hidden]{display:none}.panel::backdrop{display:none;pointer-events:none}`,
      '<slot></slot><button class="question" type="button" aria-controls="help-panel" aria-expanded="false" hidden>' +
      '<span class="glyph" aria-hidden="true"><svg viewBox="0 0 10 10" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round">' +
      '<path d="M2.8 3a2.2 1.6 0 0 1 4.4 0C7.2 4.5 5 4.7 5 6m0 2.6v.1"/></svg></span></button>' +
      '<span id="help-panel" class="panel" role="tooltip" popover="manual" hidden><span class="text"></span></span>',
      '.panel'
    );
    this.trigger = this.root.querySelector('button')!;
    this.text = this.root.querySelector('.text')!;
  }

  protected connect(signal: AbortSignal): void {
    this.attachFloat(signal);
    this.trigger.addEventListener('click', event => {
      event.preventDefault();
      event.stopPropagation();
      if (this.values.open) this.close('button');
      else if (this.visibleAnchorRect()) this.setProperty('open', true);
    }, { signal });
    const stopLayer = ModalSurface.watchLayer(() => { if (NexusTooltipElement.active === this) this.close('owner'); });
    this.panel.addEventListener('pointerenter', () => { this.reading = true; this.cancelLeave(); }, { signal });
    this.panel.addEventListener('pointerleave', () => { this.reading = false; this.leave(); }, { signal });
    window.addEventListener('blur', () => this.close('owner'), { signal });
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) this.close('owner');
    }, { signal });
    signal.addEventListener('abort', () => {
      stopLayer();
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
    this.trigger.hidden = !this.field || !this.values.content;
    this.toggleAttribute('data-field-help', !this.trigger.hidden);
    this.trigger.setAttribute('aria-label', this.ownerDocument.documentElement.lang === 'en-US' ? 'Field help' : '字段帮助');
    this.trigger.setAttribute('aria-expanded', String(this.values.open));
    this.text.textContent = String(this.values.content);
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

  protected override keepOpenOnAnchor(): boolean { return !this.field; }

  protected override place(anchorRect?: DOMRect): void {
    if (!this.values.open || this.panel.hidden || !this.anchorNode) return;
    const rect = anchorRect ?? (this.field ? this.trigger : this.anchorNode).getBoundingClientRect();
    const field = this.field?.getBoundingClientRect();
    const top = Math.min(rect.top, field?.top ?? rect.top);
    const bottom = Math.max(rect.bottom, field?.bottom ?? rect.bottom);
    const spaceAbove = top - 16, spaceBelow = innerHeight - bottom - 16;
    const height = this.text.scrollHeight + 2;
    const above = (field && height <= spaceAbove) || (height > spaceBelow && spaceAbove > spaceBelow);
    const center = rect.left + rect.width / 2;
    const width = this.panel.offsetWidth;
    const left = Math.max(8, Math.min(center - width / 2, innerWidth - width - 8));
    const pointer = Math.max(9, Math.min(center - left, width - 9));
    this.panel.dataset.side = above ? 'above' : 'below';
    this.panel.style.setProperty('--help-height', Math.max(0, above ? spaceAbove : spaceBelow) + 'px');
    this.panel.style.setProperty('--pointer', pointer + 'px');
    this.panel.style.left = left + 'px';
    this.panel.style.top = (above ? top - this.panel.offsetHeight - 8 : bottom + 8) + 'px';
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
    const labeled = anchor.querySelector<HTMLLabelElement>(':scope > .field-help-caption > label')?.control;
    this.field = anchor.matches(fieldControl) ? anchor :
      labeled && anchor.contains(labeled) && labeled.matches(fieldControl) ? labeled :
      anchor.matches('label,.form-number,.form-select') ? anchor.querySelector<HTMLElement>(fieldControl) ?? undefined : undefined;
    this.anchorOwner = new AbortController();
    const signal = this.anchorOwner.signal;
    for (const select of anchor.matches('nexus-select') ? [anchor] : anchor.querySelectorAll('nexus-select')) {
      select.shadowRoot?.addEventListener('beforetoggle', event => {
        if ((event as ToggleEvent).newState === 'open') this.close('owner');
      }, { signal, capture: true });
    }
    if (this.field) return;
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
    const control = this.field ?? anchor;
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
      if (this.isConnected && !this.hidden && this.visibleAnchorRect()) this.setProperty('open', true);
    }, Math.max(0, Number(this.values.delay)));
  }

  private leave(): void {
    if (this.field) return;
    this.cancelLeave();
    if (this.hovering || this.reading || this.anchorNode?.matches(':focus-within')) return;
    this.clearTimer();
    this.leaveTimer = window.setTimeout(() => this.close('owner'), 100);
  }

  private visibleAnchorRect(): DOMRect | undefined {
    const anchor = this.field ? this.trigger : this.anchorNode;
    if (!anchor?.isConnected || !anchor.getClientRects().length || anchor.closest('[inert]') || this.hasExpandedSelect()) return;
    const rect = anchor.getBoundingClientRect();
    let left = Math.max(0, rect.left), right = Math.min(innerWidth, rect.right);
    let top = Math.max(0, rect.top), bottom = Math.min(innerHeight, rect.bottom);
    let parent: HTMLElement | null = anchor;
    while (parent) {
      const style = getComputedStyle(parent);
      if (parent.inert || style.visibility === 'hidden') return;
      if (parent !== anchor && style.display !== 'contents') {
        const clipsX = /(hidden|clip|auto|scroll)/.test(style.overflowX);
        const clipsY = /(hidden|clip|auto|scroll)/.test(style.overflowY);
        if (clipsX || clipsY) {
          const clip = parent.getBoundingClientRect();
          if (clipsX) { left = Math.max(left, clip.left); right = Math.min(right, clip.right); }
          if (clipsY) { top = Math.max(top, clip.top); bottom = Math.min(bottom, clip.bottom); }
        }
      }
      if (parent.matches('dialog[open],:popover-open')) break;
      const tree = parent.getRootNode();
      parent = parent.assignedSlot ?? parent.parentElement ?? (tree instanceof ShadowRoot ? tree.host as HTMLElement : null);
    }
    if (right > left && bottom > top) return rect;
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
      const anchorRect = this.visibleAnchorRect();
      if (!anchorRect) { this.close('owner'); return; }
      this.place(anchorRect);
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
