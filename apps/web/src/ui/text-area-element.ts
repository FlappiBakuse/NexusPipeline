import { NexusElement, elementAttributes } from './element-base.js';
import { fieldStyles } from './control-styles.js';
import { ScrollRails, scrollRailStyles, scrollRailMarkup } from './scroll-rails.js';

export class TextArea extends NexusElement {
  static readonly observedAttributes = [...elementAttributes('nexus-text-area'), 'placeholder'];
  private readonly field: HTMLTextAreaElement;
  private composing = false;
  private height = 0;
  private readonly rails: ScrollRails;
  constructor() {
    super(
      'nexus-text-area',
      fieldStyles +
        scrollRailStyles +
        `:host{position:relative}textarea{min-height:96px;padding-bottom:12px;scrollbar-width:none}
      textarea::-webkit-scrollbar{display:none}
      textarea{padding:var(--nx-textarea-padding,8px 12px 12px)}
      .resize{position:absolute;right:3px;bottom:3px;width:14px;height:14px;min-height:14px;padding:0;border:0;border-radius:0;background:transparent;cursor:ns-resize;touch-action:none;color:var(--muted)}
      .resize svg{width:10px;height:10px;pointer-events:none}`,
      '<textarea></textarea><button class="resize" type="button"><svg viewBox="0 0 12 12" aria-hidden="true"><path d="M10 2 2 10M10 6 6 10M10 10h-.1" fill="none" stroke="currentColor"/></svg></button>' +
        scrollRailMarkup
    );
    this.field = this.root.querySelector('textarea')!;
    this.field.spellcheck = false;
    this.rails = new ScrollRails(this, this.root, this.field, this.field, () => 'both');
  }
  protected connect(signal: AbortSignal): void {
    this.rails.connect(signal);
    this.field.addEventListener(
      'compositionstart',
      () => {
        this.composing = true;
      },
      { signal }
    );
    this.field.addEventListener(
      'compositionend',
      () => {
        this.composing = false;
        this.input();
      },
      { signal }
    );
    this.field.addEventListener(
      'input',
      () => {
        if (!this.composing) this.input();
      },
      { signal }
    );
    this.field.addEventListener('change', (event) => event.stopPropagation(), { signal });
    const grip = this.root.querySelector<HTMLButtonElement>('.resize')!;
    let start: { y: number; height: number; pointer: number } | undefined;
    grip.addEventListener(
      'pointerdown',
      (event) => {
        if (event.button || this.values.disabled) return;
        start = {
          y: event.clientY,
          height: this.field.getBoundingClientRect().height,
          pointer: event.pointerId
        };
        grip.setPointerCapture(event.pointerId);
        event.preventDefault();
      },
      { signal }
    );
    grip.addEventListener(
      'pointermove',
      (event) => {
        if (start?.pointer === event.pointerId) this.resize(start.height + event.clientY - start.y);
      },
      { signal }
    );
    grip.addEventListener(
      'lostpointercapture',
      () => {
        start = undefined;
      },
      { signal }
    );
    grip.addEventListener(
      'keydown',
      (event) => {
        if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
        event.preventDefault();
        this.resize(this.field.getBoundingClientRect().height + (event.key === 'ArrowUp' ? -16 : 16));
      },
      { signal }
    );
  }
  override focus(options?: FocusOptions): void {
    this.field.focus(options);
  }
  protected render(): void {
    if (!this.composing && this.field.value !== this.values.value)
      this.field.value = String(this.values.value);
    this.field.rows = Math.max(1, Number(this.values.rows));
    this.field.readOnly = Boolean(this.values.readOnly);
    this.field.required = this.getAttribute('aria-required') === 'true';
    this.field.placeholder = this.getAttribute('placeholder') || '';
    this.field.disabled = Boolean(this.values.disabled);
    if (this.values.maxLength === null) this.field.removeAttribute('maxlength');
    else this.field.maxLength = Number(this.values.maxLength);
    this.field.setAttribute('aria-label', this.getAttribute('aria-label') ?? '');
    this.field.setAttribute('aria-invalid', this.getAttribute('aria-invalid') ?? 'false');
    this.describe(this.field);
    const grip = this.root.querySelector<HTMLButtonElement>('.resize')!;
    grip.disabled = Boolean(this.values.disabled);
    grip.setAttribute(
      'aria-label',
      this.ownerDocument.documentElement.lang.startsWith('zh') ? '调整文本框高度' : 'Resize text area'
    );
    if (this.height) this.field.style.height = this.height + 'px';
    this.rails.schedule();
  }
  private input(): void {
    if (this.field.readOnly || this.field.value === this.values.value) return;
    this.setProperty('value', this.field.value);
    this.change(this.field.value, 'input');
  }
  private resize(height: number): void {
    this.height = Math.max(40, Math.min(2000, height));
    this.field.style.height = this.height + 'px';
  }
}
