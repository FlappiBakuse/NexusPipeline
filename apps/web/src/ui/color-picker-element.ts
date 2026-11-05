import { createIdentity } from '../control/identity';
import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';

const styles =
  controlStyles +
  `
:host{display:block;min-width:0}.row{display:flex;min-width:0}.value-input{min-width:0;flex:1;height:40px;padding:0 12px;border:1px solid var(--nx-color-border);border-radius:8px 0 0 8px;background:var(--content-control);color:inherit;outline:0}
.trigger{display:flex;gap:7px;align-items:center;min-width:112px;padding:0 10px;border-radius:0 8px 8px 0;text-align:left;color:var(--nx-color-muted);font-size:12px;font-weight:400}
.swatch{width:18px;height:18px;flex:none;border-radius:50%;border:1px solid var(--border-strong,var(--nx-color-border,#394152));box-shadow:inset 0 0 0 2px var(--content-card)}
.panel{position:fixed;inset:auto;margin:0;width:260px;padding:16px;max-width:calc(100vw - 16px);border:1px solid var(--border-strong,var(--nx-color-border,#394152));border-radius:12px;background:var(--nx-color-surface,#131f33);color:inherit;box-shadow:0 16px 40px #0005}
.panel[hidden]{display:none}.palette{display:grid;grid-template-columns:repeat(6,1fr);gap:8px;margin-bottom:16px}.palette button{height:24px;min-height:24px;border-radius:5px;border:1px solid var(--border-strong,var(--nx-color-border,#394152));padding:0}
.hex{appearance:none;width:100%;height:36px;padding:8px 10px;margin-bottom:12px;background:var(--content-control,var(--nx-color-surface,#1a2940));color:inherit;border:1px solid var(--border-strong,var(--nx-color-border,#394152));border-radius:7px}
label{display:flex;align-items:center;gap:10px;font-size:12px;margin:10px 0}input[type=range]{appearance:none;width:100%;height:4px;border:0;border-radius:4px;background:var(--border-strong,var(--nx-color-border,#394152))}
input[type=range]::-webkit-slider-thumb{appearance:none;width:14px;height:14px;border-radius:50%;background:var(--nx-color-accent,#62a0ff);border:2px solid var(--nx-color-surface,#131f33)}
`;
const palette = [
  '#ffffff',
  '#cbd5e1',
  '#64748b',
  '#334155',
  '#0f172a',
  '#000000',
  '#ff727b',
  '#f59e0b',
  '#facc15',
  '#4ed39a',
  '#2eaadc',
  '#62a0ff',
  '#818cf8',
  '#a78bfa',
  '#e879f9',
  '#fb7185',
  '#14b8a6',
  '#84cc16'
];

export class ColorPicker extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-color-picker');
  private readonly trigger: HTMLButtonElement;
  private readonly panel: HTMLElement;
  private readonly hex: HTMLInputElement;
  private readonly valueField: HTMLInputElement;
  private shownValue = '';
  constructor() {
    super(
      'nexus-color-picker',
      styles,
      '<div class="row"><input class="value-input" type="text" spellcheck="false"><button class="trigger" type="button" aria-haspopup="dialog"><span class="swatch" aria-hidden="true"></span><span class="value"></span></button></div><div class="panel" popover="manual" role="dialog" hidden><div class="palette"></div><input class="hex" type="text" maxlength="7" aria-label="HEX" spellcheck="false"><div class="channels"></div></div>'
    );
    this.trigger = this.root.querySelector('.trigger')!;
    this.panel = this.root.querySelector('.panel')!;
    this.hex = this.root.querySelector('.hex')!;
    this.valueField = this.root.querySelector('.value-input')!;
    this.panel.id = 'color-' + createIdentity();
    this.trigger.setAttribute('aria-controls', this.panel.id);
    this.root.querySelector('.palette')!.replaceChildren(
      ...palette.map((color) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.dataset.color = color;
        button.style.background = color;
        button.setAttribute('aria-label', color);
        return button;
      })
    );
    this.root.querySelector('.channels')!.replaceChildren(
      ...['R', 'G', 'B'].map((channel, index) => {
        const label = document.createElement('label');
        label.textContent = channel;
        const input = document.createElement('input');
        input.type = 'range';
        input.min = '0';
        input.max = '255';
        input.step = '1';
        input.dataset.channel = String(index);
        input.setAttribute('aria-label', channel);
        label.append(input);
        return label;
      })
    );
  }
  protected connect(signal: AbortSignal): void {
    this.valueField.addEventListener(
      'change',
      (event) => {
        event.stopPropagation();
        const text = this.valueField.value;
        const value = /^#[\da-f]{3}$/i.test(text)
          ? '#' + [...text.slice(1)].map((channel) => channel + channel).join('')
          : text;
        this.valueField.setAttribute('aria-invalid', String(!/^#[\da-f]{6}$/i.test(value)));
        if (/^#[\da-f]{6}$/i.test(value)) this.commit(value);
      },
      { signal }
    );
    this.trigger.addEventListener('click', () => (this.panel.hidden ? this.open() : this.close()), {
      signal
    });
    this.hex.addEventListener(
      'change',
      (event) => {
        event.stopPropagation();
        const value = this.hex.value.startsWith('#') ? this.hex.value : '#' + this.hex.value;
        if (/^#[\da-f]{6}$/i.test(value)) this.commit(value);
        else {
          this.hex.setAttribute('aria-invalid', 'true');
        }
      },
      { signal }
    );
    this.panel.addEventListener(
      'click',
      (event) => {
        const color = (event.target as HTMLElement).closest<HTMLElement>('[data-color]')?.dataset.color;
        if (color) this.commit(color);
      },
      { signal }
    );
    this.panel.addEventListener(
      'input',
      (event) => {
        const input = event.target as HTMLInputElement;
        if (!input.matches('[data-channel]')) return;
        const values = this.channels();
        values[Number(input.dataset.channel)] = Number(input.value);
        this.commit('#' + values.map((value) => value.toString(16).padStart(2, '0')).join(''));
      },
      { signal }
    );
    this.root.addEventListener(
      'keydown',
      (event) => {
        if ((event as KeyboardEvent).key === 'Escape' && !this.panel.hidden) {
          event.preventDefault();
          event.stopPropagation();
          this.close();
          this.trigger.focus();
        }
      },
      { signal }
    );
    document.addEventListener(
      'pointerdown',
      (event) => {
        if (!event.composedPath().includes(this)) this.close();
      },
      { signal }
    );
    window.addEventListener('hashchange', () => this.close(), { signal });
    document.addEventListener(
      'focusin',
      (event) => {
        if (!event.composedPath().includes(this)) this.close();
      },
      { signal }
    );
    window.addEventListener('resize', () => this.close(), { signal });
  }
  override disconnectedCallback(): void {
    this.close();
    super.disconnectedCallback();
  }
  override focus(options?: FocusOptions): void {
    this.trigger.focus(options);
  }
  protected render(): void {
    const value = String(this.values.value);
    this.trigger.disabled = Boolean(this.values.disabled);
    this.valueField.disabled = Boolean(this.values.disabled);
    this.valueField.setAttribute('aria-label', this.getAttribute('aria-label') || 'HEX');
    const chinese = this.ownerDocument.documentElement.lang.startsWith('zh');
    this.trigger.setAttribute('aria-label', chinese ? '打开颜色选择器' : 'Open color picker');
    this.root.querySelector<HTMLElement>('.swatch')!.style.background = value;
    this.root.querySelector('.value')!.textContent = chinese ? '选择颜色' : 'Choose color';
    if (this.values.disabled) this.close();
    if (this.shownValue === value) return;
    this.shownValue = value;
    this.valueField.value = value;
    this.hex.value = value;
    const channels = this.channels();
    this.root.querySelectorAll<HTMLInputElement>('[data-channel]').forEach((input, index) => {
      input.value = String(channels[index]);
    });
  }
  private channels(): number[] {
    const color = String(this.values.value).replace('#', '').padEnd(6, '0');
    return [0, 2, 4].map((index) => parseInt(color.slice(index, index + 2), 16) || 0);
  }
  private commit(value: string): void {
    if (this.values.disabled) return;
    this.hex.setAttribute('aria-invalid', 'false');
    this.setProperty('value', value.toLowerCase());
    this.change(value.toLowerCase(), 'input');
  }
  private open(): void {
    if (this.values.disabled) return;
    this.panel.hidden = false;
    this.panel.showPopover();
    this.trigger.setAttribute('aria-expanded', 'true');
    const rect = this.getBoundingClientRect();
    this.panel.style.left = Math.max(8, Math.min(rect.left, innerWidth - this.panel.offsetWidth - 8)) + 'px';
    this.panel.style.top =
      Math.max(
        8,
        rect.bottom + this.panel.offsetHeight < innerHeight
          ? rect.bottom + 5
          : rect.top - this.panel.offsetHeight - 5
      ) + 'px';
    this.hex.focus();
  }
  private close(): void {
    if (this.panel.matches(':popover-open')) this.panel.hidePopover();
    this.panel.hidden = true;
    this.trigger.setAttribute('aria-expanded', 'false');
  }
}
