import { NexusElement, elementAttributes } from './element-base.js';
import { ModalSurface } from './modal-surface.js';
import { joinToastStack } from './toast-stack.js';

export class NexusToastElement extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-toast');
  private timer: ReturnType<typeof setTimeout> | undefined;
  private lastMessage = '';
  private animation?: Animation;
  private stack?: AbortController;
  constructor() {
    super('nexus-toast', `
      :host{position:fixed;inset:auto 24px var(--nx-toast-bottom,24px) auto;margin:0;max-width:calc(100vw - 48px);
      box-sizing:border-box;padding:12px 16px;border:1px solid var(--nx-color-border);border-radius:8px;
      background:var(--nx-color-surface,#1e2330);color:var(--nx-color-text,#e9edf5);box-shadow:0 12px 28px #0005;
      white-space:pre-wrap;overflow-wrap:anywhere;pointer-events:none}
      :host(:not(:popover-open)){display:none}
      :host([data-tone=danger]){border-color:var(--nx-color-danger)}
      :host([data-tone=warning]){border-color:var(--warning)}
      :host([data-tone=success]){border-color:var(--success)}`, '<span></span>');
  }
  protected connect(signal: AbortSignal): void {
    this.popover = 'manual';
    this.lastMessage = '';
    const stop = ModalSurface.watchLayer(() => {
      // Re-enter the top layer after a new modal, without stealing its focus.
      if (this.matches(':popover-open')) { this.hidePopover(); this.showPopover(); }
    });
    signal.addEventListener('abort', () => {
      stop(); clearTimeout(this.timer); this.animation?.cancel(); this.stack?.abort();
    }, { once: true });
  }
  protected render(): void {
    this.dataset.tone = String(this.values.tone);
    const message = String(this.values.message);
    const code = /^[a-z][a-z0-9]*(?:_[a-z0-9]+)+$/.test(message);
    this.root.querySelector('span')!.textContent = code
      ? (this.ownerDocument.documentElement.lang === 'en-US'
        ? 'The operation could not be completed. Diagnostic code: '
        : '操作未完成，请查看诊断详情。错误码：') + message
      : message;
    this.setAttribute('role', this.values.tone === 'danger' ? 'alert' : 'status');
    if (message === this.lastMessage) return;
    this.lastMessage = message;
    clearTimeout(this.timer); this.animation?.cancel();
    if (!message) { this.dismiss(); return; }
    this.showPopover();
    this.stack?.abort(); this.stack = new AbortController();
    joinToastStack(this, this.stack.signal);
    this.animation = this.animate([{ opacity: 0, transform: 'translateY(24px)' }, { opacity: 1, transform: 'none' }],
      { duration: this.duration(), easing: 'cubic-bezier(.2,.8,.2,1)' });
    this.timer = setTimeout(() => {
      this.animation = this.animate([{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'translateY(24px)' }],
        { duration: this.duration(), easing: 'ease-in', fill: 'forwards' });
      void this.animation.finished.then(() => { this.dismiss(); this.emit('nexus-close', { reason: 'programmatic' }); }, () => {});
    }, ['warning', 'danger'].includes(String(this.values.tone)) ? 6000 : 4000);
  }
  private duration(): number {
    if (matchMedia('(prefers-reduced-motion: reduce)').matches) return 0;
    const value = getComputedStyle(this).getPropertyValue('--nx-motion-duration-normal').trim();
    return value ? parseFloat(value) * (value.endsWith('ms') ? 1 : 1000) : 200;
  }
  private dismiss(): void {
    if (this.matches(':popover-open')) this.hidePopover();
    this.stack?.abort();
  }
}
