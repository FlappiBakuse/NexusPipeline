import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';

const styles =
  controlStyles +
  `
:host{display:flex;min-width:0;gap:8px}input{min-width:0;min-height:40px;padding:8px 12px;border:1px solid var(--nx-color-border,#394152);
border-radius:var(--nx-radius-sm,8px);background:var(--content-control,#ffffff08)}
`;

export { DateRangePicker } from './range-picker.js';

export class PathPicker extends NexusElement {
  static readonly observedAttributes = [...elementAttributes('nexus-path-picker'), 'placeholder'];
  private readonly field: HTMLInputElement;
  private readonly actions: HTMLElement;
  constructor() {
    super(
      'nexus-path-picker',
      styles +
        `:host{width:100%;gap:0}input{flex:1;border-radius:8px 0 0 8px}
      .actions{display:flex}button{display:grid;place-items:center;min-width:42px;width:42px;padding:0;border-radius:0}
      button:last-child{border-radius:0 8px 8px 0}svg{width:16px;height:16px;fill:none;stroke:currentColor;stroke-width:1.6}`,
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
        if (this.field.value === this.values.value || this.values.readOnly) return;
        this.setProperty('value', this.field.value);
        this.change(this.field.value, 'input');
      },
      { signal }
    );
    this.actions.addEventListener(
      'click',
      (event) => {
        const kind = (event.target as HTMLElement).closest<HTMLButtonElement>('button')?.dataset.kind;
        if (kind && this.values.browseAllowed) this.emit('nexus-browse', { kind });
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
        button.disabled = !this.values.browseAllowed;
        return button;
      })
    );
    this.describe(this.field);
  }
}
