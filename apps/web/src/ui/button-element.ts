import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';

export class NexusButtonElement extends NexusElement {
  static readonly formAssociated = true;
  static readonly observedAttributes = [...elementAttributes('nexus-button'), 'aria-pressed'];
  private readonly button: HTMLButtonElement;
  private readonly fallback: Text;
  private readonly internals: ElementInternals;

  constructor() {
    super(
      'nexus-button',
      controlStyles +
        `button{width:100%;min-width:0;padding:var(--nx-button-padding,0 15px);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;background:var(--nx-button-fill,transparent)}
      button[data-tone=danger]{background:var(--bad-soft,transparent);border-color:color-mix(in srgb,var(--nx-color-danger) 45%,transparent)}
    `,
      '<button><slot name="icon"></slot><slot></slot></button>'
    );
    this.button = this.root.querySelector('button')!;
    this.internals = this.attachInternals();
    this.fallback = document.createTextNode('');
    this.root.querySelector('slot:not([name])')!.append(this.fallback);
  }

  protected connect(signal: AbortSignal): void {
    this.addEventListener(
      'click',
      (event) => {
        if (this.values.disabled || this.values.busy) {
          event.preventDefault();
          event.stopImmediatePropagation();
        }
      },
      { capture: true, signal }
    );
    this.button.addEventListener(
      'click',
      (event) => {
        queueMicrotask(() => {
          if (event.defaultPrevented || this.values.disabled || this.values.busy || signal.aborted) return;
          if (this.values.type === 'submit') this.internals.form?.requestSubmit();
          if (this.values.type === 'reset') this.internals.form?.reset();
        });
      },
      { signal }
    );
  }

  override focus(options?: FocusOptions): void {
    this.button.focus(options);
  }

  protected render(): void {
    this.button.type = 'button';
    this.button.disabled = Boolean(this.values.disabled || this.values.busy);
    this.button.setAttribute('aria-busy', String(this.values.busy));
    this.button.dataset.tone = String(this.values.tone);
    this.fallback.data = String(this.values.label);
    const name = this.getAttribute('aria-label');
    if (name !== null) this.button.setAttribute('aria-label', name);
    else this.button.removeAttribute('aria-label');
    const pressed = this.getAttribute('aria-pressed');
    if (pressed === null) this.button.removeAttribute('aria-pressed');
    else this.button.setAttribute('aria-pressed', pressed);
    this.describe(this.button);
  }
}
