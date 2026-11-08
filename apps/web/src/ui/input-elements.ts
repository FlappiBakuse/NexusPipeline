import { NexusElement, elementAttributes } from './element-base.js';
import { TimeControl } from './time-control.js';
import { fieldStyles } from './control-styles.js';

export { TextArea } from './text-area-element.js';

export class NumberInput extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-number-input');
  private readonly field: HTMLInputElement;
  private shownValue: unknown = undefined;
  private invalidText = false;
  private writingValidity = false;
  constructor() {
    super(
      'nexus-number-input',
      fieldStyles +
        `:host{width:100%;position:relative}input{padding-right:48px}.step{position:absolute;right:1px;width:42px;min-height:19px;height:19px;padding:0;border-radius:0;border:0;border-left:1px solid var(--nx-color-border);font-size:14px;background:transparent}.step:first-of-type{bottom:1px;border-radius:0 0 7px 0}.step:last-of-type{top:1px;border-bottom:1px solid var(--nx-color-border);border-radius:0 7px 0 0}`,
      '<button class="step" type="button" aria-label="Decrease">−</button><input type="number"><button class="step" type="button" aria-label="Increase">+</button>'
    );
    this.field = this.root.querySelector('input')!;
  }
  protected connect(signal: AbortSignal): void {
    this.field.addEventListener(
      'change',
      (event) => {
        event.stopPropagation();
        this.commit('input');
      },
      { signal }
    );
    this.field.addEventListener(
      'keydown',
      (event) => {
        if (event.key === 'Enter') this.commit('input');
      },
      { signal }
    );
    this.root.querySelectorAll('button').forEach((button, index) =>
      button.addEventListener(
        'click',
        (event) => {
          if (this.values.readOnly) return;
          const current = this.values.value === null ? 0 : Number(this.values.value);
          const step = Number(this.values.step);
          const next = this.clamp(current + (index ? step : -step));
          if (next === current) return;
          this.field.setCustomValidity('');
          this.setProperty('value', next);
          this.change(next, event.detail === 0 ? 'keyboard' : 'pointer');
        },
        { signal }
      )
    );
  }
  override focus(options?: FocusOptions): void {
    this.field.focus(options);
  }
  override attributeChangedCallback(name: string, oldValue: string | null, next: string | null): void {
    if (!this.writingValidity) super.attributeChangedCallback(name, oldValue, next);
  }
  protected render(): void {
    if (this.shownValue !== this.values.value) {
      this.shownValue = this.values.value;
      this.validity(true);
      this.field.setCustomValidity('');
    }
    if (this.root.activeElement !== this.field && !this.invalidText)
      this.field.value = this.values.value === null ? '' : String(this.values.value);
    this.field.min = this.values.min === null ? '' : String(this.values.min);
    this.field.max = this.values.max === null ? '' : String(this.values.max);
    this.field.step = String(this.values.step);
    this.field.required = this.getAttribute('aria-required') === 'true';
    this.field.readOnly = Boolean(this.values.readOnly);
    this.field.setAttribute('aria-invalid', this.getAttribute('aria-invalid') ?? 'false');
    this.field.setAttribute('aria-label', this.getAttribute('aria-label') ?? '');
    this.root.querySelectorAll('button').forEach((button, index) => {
      const chinese = this.ownerDocument.documentElement.lang.startsWith('zh');
      button.setAttribute('aria-label', index ? (chinese ? '增加' : 'Increase') : (chinese ? '减少' : 'Decrease'));
      const current = this.values.value;
      button.disabled =
        Boolean(this.values.readOnly) ||
        (current !== null &&
          (index
            ? this.values.max !== null && Number(current) >= Number(this.values.max)
            : this.values.min !== null && Number(current) <= Number(this.values.min)));
    });
    this.describe(this.field);
  }
  private clamp(value: number): number {
    if (this.values.min !== null) value = Math.max(value, Number(this.values.min));
    if (this.values.max !== null) value = Math.min(value, Number(this.values.max));
    return value;
  }
  private commit(source: 'input'): void {
    if (this.values.readOnly) return;
    this.field.setCustomValidity('');
    if (!this.field.checkValidity()) {
      this.validity(false);
      return;
    }
    const next = this.field.value === '' ? null : this.field.valueAsNumber;
    if (next !== null && !Number.isFinite(next)) {
      this.validity(false);
      return;
    }
    if (
      next !== null &&
      ((this.values.min !== null && next < Number(this.values.min)) ||
        (this.values.max !== null && next > Number(this.values.max)))
    ) {
      this.field.setCustomValidity(this.ownerDocument.documentElement.lang.startsWith('zh')
        ? '数值超出允许范围' : 'Value is outside the allowed range');
      this.validity(false);
      return;
    }
    this.field.setCustomValidity('');
    this.validity(true);
    const value = next;
    if (value === this.values.value) return;
    this.setProperty('value', value);
    this.change(value, source);
  }
  private validity(valid: boolean): void {
    this.invalidText = !valid;
    this.writingValidity = true;
    this.setAttribute('aria-invalid', String(!valid));
    this.writingValidity = false;
    this.field.setAttribute('aria-invalid', String(!valid));
  }
}

export class NexusRangeElement extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-range');
  private readonly field: HTMLInputElement;
  private source: 'pointer' | 'keyboard' = 'keyboard';
  constructor() {
    super(
      'nexus-range',
      fieldStyles +
        `
      :host{width:100%;height:40px;align-items:center}.range{position:relative;width:100%;height:40px}
      .range-track{position:absolute;inset:16px 0 auto;height:8px;border:1px solid light-dark(#b2b2b2,#999);border-radius:999px;
      background-color:light-dark(#efefef,#3b3b3b);background-image:linear-gradient(to right,var(--nx-color-accent) 0 var(--range-progress),transparent var(--range-progress) 100%)}
      .range-thumb{position:absolute;top:12px;left:calc((100% - 16px) * var(--range-unit));width:16px;height:16px;border-radius:50%;background:var(--nx-color-accent)}
      .range:focus-within{outline:2px solid var(--nx-color-accent);outline-offset:-2px;border-radius:4px}
      input{position:absolute;inset:0;appearance:none;opacity:0;width:100%;height:40px;min-height:40px;margin:0;padding:0;border:0;background:transparent;cursor:pointer}
      .range-track,.range-thumb{pointer-events:none}
    `,
      '<div class="range"><span class="range-track" aria-hidden="true"></span><span class="range-thumb" aria-hidden="true"></span><input type="range"></div>'
    );
    this.field = this.root.querySelector('input')!;
  }
  protected connect(signal: AbortSignal): void {
    this.field.addEventListener(
      'pointerdown',
      () => {
        this.source = 'pointer';
      },
      { signal }
    );
    this.field.addEventListener(
      'keydown',
      () => {
        this.source = 'keyboard';
      },
      { signal }
    );
    this.field.addEventListener(
      'input',
      () => {
        this.update('nexus-change');
      },
      { signal }
    );
    this.field.addEventListener(
      'change',
      (event) => {
        event.stopPropagation();
        this.update('nexus-commit');
      },
      { signal }
    );
  }
  override focus(options?: FocusOptions): void {
    this.field.focus(options);
  }
  protected render(): void {
    this.field.min = String(this.values.min);
    this.field.max = String(this.values.max);
    this.field.step = String(this.values.step);
    this.field.value = String(this.values.value);
    const span = Number(this.values.max) - Number(this.values.min);
    const progress = span > 0 ? ((this.field.valueAsNumber - Number(this.values.min)) / span) * 100 : 0;
    const range = this.root.querySelector<HTMLElement>('.range')!;
    range.style.setProperty('--range-progress', Math.max(0, Math.min(100, progress)) + '%');
    range.style.setProperty('--range-unit', String(Math.max(0, Math.min(1, progress / 100))));
    this.field.setAttribute('aria-label', this.getAttribute('aria-label') ?? '');
    this.describe(this.field);
  }
  private update(name: string): void {
    const value = this.field.valueAsNumber;
    if (name === 'nexus-change') this.setProperty('value', value);
    this.emit(name, { value, source: this.source });
  }
}

export class FilePicker extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-file-picker');
  private readonly field: HTMLInputElement;
  private fileNames = '';
  constructor() {
    super(
      'nexus-file-picker',
      fieldStyles +
        `:host{width:auto;max-width:100%;align-items:center;gap:8px}
      input{position:absolute;opacity:0;width:1px;height:1px}button{flex:none;width:auto}
      .filename{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:12px;color:var(--nx-color-muted)}`,
      '<button type="button"><slot name="label"></slot></button><span class="filename"></span><input type="file">'
    );
    this.field = this.root.querySelector('input')!;
    this.field.tabIndex = -1;
    this.field.setAttribute('aria-hidden', 'true');
  }
  protected connect(signal: AbortSignal): void {
    this.root.querySelector('button')!.addEventListener('click', () => this.field.click(), { signal });
    this.field.addEventListener(
      'change',
      (event) => {
        event.stopPropagation();
        const files = [...(this.field.files ?? [])];
        this.fileNames = files.map((file) => file.name).join(', ');
        this.render();
        this.change(Object.freeze(files), 'input');
        this.field.value = '';
      },
      { signal }
    );
  }
  override focus(options?: FocusOptions): void {
    this.root.querySelector('button')!.focus(options);
  }
  protected render(): void {
    this.field.accept = String(this.values.accept);
    this.field.multiple = Boolean(this.values.multiple);
    this.field.disabled = Boolean(this.values.disabled);
    const button = this.root.querySelector('button')!;
    button.disabled = Boolean(this.values.disabled);
    const label = this.getAttribute('aria-label');
    if (label === null) button.removeAttribute('aria-label');
    else button.setAttribute('aria-label', label);
    const chinese = this.ownerDocument.documentElement.lang.startsWith('zh');
    this.root.querySelector('slot')!.textContent = label || (chinese ? '选择文件' : 'Choose file');
    this.root.querySelector('.filename')!.textContent =
      this.fileNames || (chinese ? '未选择文件' : 'No file selected');
  }
}

export { ColorPicker } from './color-picker-element.js';

export class TimePicker extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-time-picker');
  private control?: TimeControl;
  constructor() {
    super('nexus-time-picker', ':host{display:inline-block;width:100%}', '<span></span>');
  }
  protected connect(_signal: AbortSignal): void {
    const host = this.root.querySelector('span')!;
    this.control = new TimeControl(
      host.shadowRoot || host.attachShadow({ mode: 'open', delegatesFocus: true }),
      (value) => {
        this.setProperty('value', value);
        this.change(value, 'input');
      }
    );
  }
  override disconnectedCallback(): void {
    this.control?.dispose();
    super.disconnectedCallback();
  }
  override focus(options?: FocusOptions): void {
    this.control?.field.focus(options);
  }
  protected render(): void {
    this.control?.set({
      value: String(this.values.value),
      label:
        this.getAttribute('aria-label') ||
        (this.ownerDocument.documentElement.lang.startsWith('zh') ? '时间' : 'Time'),
      disabled: Boolean(this.values.disabled)
    });
  }
}
