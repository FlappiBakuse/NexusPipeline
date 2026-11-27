import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles, compositeFieldStyles } from './control-styles.js';

const styles =
  controlStyles +
  `
:host{display:flex;width:100%;min-width:0;box-shadow:var(--nx-surface-shadow)}input{flex:1;min-width:0;min-height:40px;padding:8px 12px;border:1px solid var(--nx-color-border,#394152);
border-radius:var(--nx-radius-sm);background:var(--content-embedded-fill,var(--content-control,#ffffff08));
  }
.actions{display:flex}
input:has(+ .actions > button){border-start-end-radius:0;border-end-end-radius:0}
button{display:grid;place-items:center;min-width:42px;width:42px;padding:0;border-inline-start:0;background:var(--content-embedded-fill,var(--content-control,#ffffff08));border-radius:0;box-shadow:none}
button:last-child{border-start-end-radius:var(--nx-radius-sm);border-end-end-radius:var(--nx-radius-sm)}
svg{width:16px;height:16px;fill:none;stroke:currentColor;stroke-width:1.6}
`;

export class PathPicker extends NexusElement {
  static readonly observedAttributes = [...elementAttributes('nexus-path-picker'), 'placeholder'];
  private readonly field: HTMLInputElement;
  private readonly actions: HTMLElement;
  constructor() {
    super(
      'nexus-path-picker',
      styles + compositeFieldStyles,
      '<input type="text"><span class="actions"></span>'
    );
    this.field = this.root.querySelector('input')!;
    this.actions = this.root.querySelector('.actions')!;
  }
  protected connect(signal: AbortSignal): void {
    this.field.addEventListener(
      'change',
      (event) => {
        event.stopPropagation();
        if (this.field.value === this.values.value || this.values.readOnly || this.values.disabled) return;
        this.setProperty('value', this.field.value);
        this.change(this.field.value, 'input');
      },
      { signal }
    );
    this.actions.addEventListener(
      'click',
      (event) => {
        const kind = (event.target as HTMLElement).closest<HTMLButtonElement>('button')?.dataset.kind;
        if (kind && this.values.browseAllowed && !this.values.disabled && !this.values.readOnly)
          this.emit('nexus-browse', { kind });
      },
      { signal }
    );
  }
  override focus(options?: FocusOptions): void {
    this.field.focus(options);
  }
  protected render(): void {
    if (this.root.activeElement !== this.field) this.field.value = String(this.values.value);
    this.field.readOnly = Boolean(this.values.readOnly);
    this.field.disabled = Boolean(this.values.disabled);
    this.field.required = this.getAttribute('aria-required') === 'true';
    this.field.setAttribute('aria-invalid', this.getAttribute('aria-invalid') ?? 'false');
    this.field.placeholder = this.getAttribute('placeholder') || '';
    this.field.setAttribute('aria-label', this.getAttribute('aria-label') ?? 'Path');
    this.actions.replaceChildren(
      ...(this.values.kinds as readonly string[]).map((kind) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.dataset.kind = kind;
        const chinese = this.ownerDocument.documentElement.lang.startsWith('zh');
        button.setAttribute(
          'aria-label',
          kind === 'file' ? (chinese ? '选择文件' : 'Choose file') : chinese ? '选择文件夹' : 'Choose folder'
        );
        button.innerHTML =
          '<svg viewBox="0 0 24 24" aria-hidden="true">' +
          (kind === 'file'
            ? '<path d="M6 3h8l4 4v14H6zM14 3v5h4M9 12h6M9 16h6"/>'
            : '<path d="M3 7h6l2 2h10v11H3zM3 7V4h6l2 2h8v3"/>') +
          '</svg>';
        button.disabled = Boolean(this.values.disabled || this.values.readOnly) || !this.values.browseAllowed;
        return button;
      })
    );
    this.describe(this.field);
  }
}
