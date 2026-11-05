import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';

const styles =
  controlStyles +
  `
:host{display:block;width:100%}
input{width:100%;min-height:var(--nx-control-height,40px);border:1px solid var(--nx-color-border,#394152);
border-radius:var(--nx-radius-sm,8px);padding:0 var(--nx-input-padding-end,var(--nx-space-3,12px)) 0 var(--nx-space-3,12px);background:var(--input-bg,var(--content-control,#ffffff08))}
input[aria-invalid=true]{border-color:var(--nx-color-danger,#ef7070)}
`;

export class TextInput extends NexusElement {
  static readonly formAssociated = true;
  static readonly observedAttributes = [...elementAttributes('nexus-text-input'), 'name'];
  private readonly input: HTMLInputElement;
  private readonly internals: ElementInternals;
  private composing = false;
  private lastUserValue = '';
  private formDisabled = false;

  constructor() {
    super('nexus-text-input', styles, '<input>');
    this.input = this.root.querySelector('input')!;
    this.internals = this.attachInternals();
  }

  protected connect(signal: AbortSignal): void {
    this.input.addEventListener(
      'compositionstart',
      () => {
        this.composing = true;
      },
      { signal }
    );
    this.input.addEventListener(
      'compositionend',
      () => {
        this.composing = false;
        this.userInput();
      },
      { signal }
    );
    this.input.addEventListener(
      'input',
      () => {
        if (!this.composing) this.userInput();
      },
      { signal }
    );
    this.input.addEventListener('change', (event) => event.stopPropagation(), { signal });
  }

  formDisabledCallback(disabled: boolean): void {
    this.formDisabled = disabled;
    if (this.isConnected) this.render();
  }

  formResetCallback(): void {
    this.setProperty('value', '');
  }
  formStateRestoreCallback(state: string | File | FormData | null): void {
    if (this.values.type !== 'password' && typeof state === 'string') this.setProperty('value', state);
  }

  override focus(options?: FocusOptions): void {
    this.input.focus(options);
  }

  protected render(): void {
    this.input.type = String(this.values.type);
    const value = String(this.values.value);
    if (this.input.value !== value && !this.composing) this.input.value = value;
    this.lastUserValue = value;
    this.input.placeholder = String(this.values.placeholder);
    this.input.readOnly = Boolean(this.values.readOnly);
    this.input.disabled = this.formDisabled || Boolean(this.values.disabled);
    this.input.required = this.getAttribute('aria-required') === 'true';
    this.input.setAttribute('aria-required', String(this.input.required));
    if (this.values.maxLength === null) this.input.removeAttribute('maxlength');
    else this.input.maxLength = Number(this.values.maxLength);
    this.internals.setFormValue(value, this.values.type === 'password' ? null : value);
    this.internals.setValidity(this.input.validity, this.input.validationMessage, this.input);
    const labels = [...this.internals.labels].map((label) => label.textContent ?? '').join(' ');
    this.input.setAttribute('aria-label', this.getAttribute('aria-label') ?? labels);
    this.input.setAttribute('aria-invalid', this.getAttribute('aria-invalid') ?? 'false');
    this.describe(this.input);
  }

  private userInput(): void {
    if (this.input.disabled || this.input.readOnly || this.input.value === this.lastUserValue) return;
    const value = this.input.value;
    this.setProperty('value', value);
    this.change(value, 'input');
  }
}
