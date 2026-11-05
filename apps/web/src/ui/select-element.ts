import { createIdentity } from '../control/identity';
import type { Option } from '@nexuspipeline/extension-sdk';
import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';

const styles =
  controlStyles +
  `
:host{display:inline-block;min-width:0;width:100%}.field{position:relative}
input{appearance:none;width:100%;height:40px;padding:0 38px 0 12px;border:1px solid var(--border-strong,var(--nx-color-border,#394152));
border-radius:8px;background:var(--content-control,var(--nx-color-surface,#1a2940));font:inherit;font-weight:400;color:inherit;cursor:pointer;outline:0}
.arrow{position:absolute;right:15px;top:16px;width:7px;height:7px;border:solid var(--nx-color-muted);border-width:0 1.5px 1.5px 0;transform:rotate(45deg);pointer-events:none}
input[aria-expanded=true]{border-color:var(--nx-color-accent)}input[aria-expanded=true]+.arrow{transform:translateY(3px) rotate(225deg)}
input[aria-invalid=true]{border-color:var(--nx-color-danger)}
input::placeholder{color:inherit;opacity:1}
.panel{position:fixed;inset:auto;margin:0;padding:5px;max-width:calc(100vw - 16px);max-height:300px;overflow:hidden;
border:1px solid var(--border-strong,var(--nx-color-border,#394152));border-radius:10px;background:var(--nx-color-surface,#131f33);color:inherit;box-shadow:0 14px 40px #0004}
.panel[hidden]{display:none}.panel::backdrop{background:transparent}
.option{display:flex;align-items:center;gap:12px;width:100%;min-height:36px;text-align:left;padding:7px 10px;border:0;background:transparent;line-height:1.5;border-radius:6px}
.option[aria-selected=true]{color:var(--nx-color-accent);background:var(--accent-soft,color-mix(in srgb,var(--nx-color-accent,#62a0ff) 15%,transparent))}
.option[aria-selected=true]::after{content:'';margin-left:auto;flex-shrink:0;width:6px;height:10px;border:solid currentColor;border-width:0 1.5px 1.5px 0;transform:rotate(45deg)}
.option:focus-visible{outline-offset:-2px}.empty{padding:12px;color:var(--nx-color-muted);font-size:12px}
@media(max-width:600px){input{min-height:44px}}
`;

export class NexusSelectElement extends NexusElement {
  static readonly observedAttributes = [...elementAttributes('nexus-select'), 'placeholder'];
  private readonly field: HTMLInputElement;
  private readonly list: HTMLElement;
  private expanded = false;
  private query = '';
  private drawn = '';
  private start = 0;
  private readonly windowSize = 80;
  private composing = false;
  private anchor: DOMRect | undefined;

  constructor() {
    super(
      'nexus-select',
      styles,
      '<div class="field"><input role="combobox" aria-expanded="false" autocomplete="off"><span class="arrow" aria-hidden="true"></span></div><nexus-scroll-area class="panel" label="Options" role="listbox" popover="manual" hidden></nexus-scroll-area>'
    );
    this.field = this.root.querySelector('input')!;
    this.list = this.root.querySelector('.panel')!;
    this.list.id = 'options-' + createIdentity();
    this.field.setAttribute('aria-controls', this.list.id);
    this.field.setAttribute('aria-haspopup', 'listbox');
  }

  protected connect(signal: AbortSignal): void {
    this.field.addEventListener('click', () => (this.expanded ? this.shut() : this.open()), { signal });
    const search = () => {
      if (this.composing) return;
      this.query = this.field.value;
      this.start = 0;
      this.open(false);
      this.draw();
    };
    this.field.addEventListener(
      'beforeinput',
      (event) => {
        if (
          !this.expanded &&
          this.values.searchable &&
          !this.composing &&
          event.inputType.startsWith('insert')
        )
          this.open();
      },
      { signal }
    );
    this.field.addEventListener(
      'compositionstart',
      () => {
        if (!this.expanded && this.values.searchable) this.open();
        this.composing = true;
      },
      { signal }
    );
    this.field.addEventListener(
      'compositionend',
      () => {
        this.composing = false;
        search();
      },
      { signal }
    );
    this.field.addEventListener('input', search, { signal });
    this.field.addEventListener('keydown', (event) => this.key(event, true), { signal });
    this.list.addEventListener('keydown', (event) => this.key(event, false), { signal });
    this.list.addEventListener(
      'click',
      (event) => {
        const button = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-id]');
        if (button && !button.disabled)
          this.choose(button.dataset.id!, event.detail ? 'pointer' : 'keyboard');
        const page = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-page]');
        if (page && !page.disabled) {
          this.start += Number(page.dataset.page) * this.windowSize;
          this.draw();
        }
      },
      { signal }
    );
    document.addEventListener(
      'pointerdown',
      (event) => {
        if (!event.composedPath().includes(this)) this.shut();
      },
      { signal }
    );
    document.addEventListener(
      'visibilitychange',
      () => {
        if (document.hidden) this.shut();
      },
      { signal }
    );
    window.addEventListener('resize', () => this.shut(), { signal });
    window.addEventListener('hashchange', () => this.shut(), { signal });
    window.addEventListener(
      'scroll',
      (event) => {
        if (!this.expanded || !this.anchor || event.composedPath().includes(this.list)) return;
        const rect = this.getBoundingClientRect();
        if (rect.top !== this.anchor.top || rect.left !== this.anchor.left) this.shut();
      },
      { signal, capture: true }
    );
    document.addEventListener(
      'focusin',
      (event) => {
        if (!event.composedPath().includes(this)) this.shut();
      },
      { signal }
    );
  }

  override disconnectedCallback(): void {
    this.shut();
    super.disconnectedCallback();
  }
  override focus(options?: FocusOptions): void {
    this.field.focus(options);
  }

  protected render(): void {
    const options = this.values.options as readonly Option[];
    const selected = this.selection();
    if (!this.expanded)
      this.field.value = selected
        .map((id) => options.find((option) => option.id === id)?.label || '')
        .filter(Boolean)
        .join(', ');
    this.field.readOnly = !this.values.searchable;
    this.field.placeholder = this.getAttribute('placeholder') || '';
    this.field.setAttribute('aria-autocomplete', this.values.searchable ? 'list' : 'none');
    this.field.setAttribute('aria-required', this.getAttribute('aria-required') || 'false');
    this.field.setAttribute('aria-invalid', this.getAttribute('aria-invalid') || 'false');
    this.field.setAttribute(
      'aria-label',
      this.getAttribute('aria-label') || this.closest('label')?.textContent?.trim() || 'Select option'
    );
    this.list.setAttribute('aria-multiselectable', String(this.values.multiple));
    this.list.setAttribute('label', this.field.getAttribute('aria-label')!);
    this.describe(this.field);
    this.draw();
  }

  private selection(): readonly string[] {
    return Array.isArray(this.values.value) ? (this.values.value as string[]) : [String(this.values.value)];
  }

  private open(clearQuery = true): void {
    if (this.expanded || this.closest('[inert]')) return;
    this.expanded = true;
    this.drawn = '';
    if (clearQuery) {
      this.start = 0;
      if (this.values.searchable) {
        this.query = '';
        this.field.value = '';
      }
    }
    this.field.setAttribute('aria-expanded', 'true');
    this.draw();
  }

  private shut(): void {
    if (!this.expanded) return;
    this.expanded = false;
    this.query = '';
    this.anchor = undefined;
    if (this.list.matches(':popover-open')) this.list.hidePopover();
    this.list.hidden = true;
    this.field.setAttribute('aria-expanded', 'false');
    this.render();
  }

  private draw(): void {
    if (!this.expanded) return;
    const selected = this.selection();
    const options = (this.values.options as readonly Option[]).filter((item) =>
      item.label.toLocaleLowerCase().includes(this.query.toLocaleLowerCase())
    );
    this.start = Math.max(
      0,
      Math.min(this.start, Math.max(0, Math.floor((options.length - 1) / this.windowSize) * this.windowSize))
    );
    const shown = options.slice(this.start, this.start + this.windowSize);
    const fingerprint = JSON.stringify([selected, shown, this.start, options.length, this.ownerDocument.documentElement.lang]);
    if (this.drawn === fingerprint) return;
    this.drawn = fingerprint;
    const focused = (this.root.activeElement as HTMLElement | null)?.dataset.id;
    this.list.replaceChildren(
      ...shown.map((item, index) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.role = 'option';
        button.className = 'option';
        button.dataset.id = item.id;
        button.disabled = Boolean(item.disabled);
        button.setAttribute('aria-selected', String(selected.includes(item.id)));
        button.setAttribute('aria-posinset', String(this.start + index + 1));
        button.setAttribute('aria-setsize', String(options.length));
        button.textContent = item.label;
        return button;
      })
    );
    if (options.length > this.windowSize) {
      for (const [direction, label] of [
        [-1, 'Previous options'],
        [1, 'Next options']
      ] as const) {
        const button = document.createElement('button');
        button.type = 'button';
        button.dataset.page = String(direction);
        button.textContent = this.ownerDocument.documentElement.lang.startsWith('zh')
          ? direction < 0
            ? '上一页选项'
            : '下一页选项'
          : label;
        button.disabled = direction < 0 ? this.start === 0 : this.start + this.windowSize >= options.length;
        this.list.append(button);
      }
    }
    if (!options.length) {
      const empty = document.createElement('span');
      empty.className = 'empty';
      empty.textContent = this.ownerDocument.documentElement.lang.startsWith('zh')
        ? '没有匹配的选项'
        : 'No matching options';
      this.list.append(empty);
    }
    this.list.hidden = false;
    if (!this.list.matches(':popover-open')) this.list.showPopover();
    const rect = this.getBoundingClientRect();
    this.anchor = rect;
    this.list.style.minWidth = Math.min(rect.width, innerWidth - 16) + 'px';
    this.list.style.left = Math.max(8, Math.min(rect.left, innerWidth - this.list.offsetWidth - 8)) + 'px';
    const below = innerHeight - rect.bottom - 8,
      above = rect.top - 8;
    const height = Math.min(300, Math.max(below, above));
    this.list.style.maxHeight = Math.max(40, height) + 'px';
    this.list.style.top =
      (below >= Math.min(300, this.list.scrollHeight) || below >= above
        ? rect.bottom + 4
        : Math.max(8, rect.top - this.list.offsetHeight - 4)) + 'px';
    if (focused)
      [...this.list.querySelectorAll<HTMLButtonElement>('button')]
        .find((button) => button.dataset.id === focused)
        ?.focus();
  }

  private key(event: KeyboardEvent, fromField: boolean): void {
    if (this.composing || event.isComposing) return;
    if (event.key === 'Escape' && this.expanded) {
      event.preventDefault();
      event.stopPropagation();
      this.shut();
      this.field.focus();
      return;
    }
    if (event.key === 'Tab') {
      this.shut();
      this.field.focus();
      return;
    }
    if (fromField && ['Enter', ' '].includes(event.key) && !this.values.searchable) {
      event.preventDefault();
      this.expanded ? this.shut() : this.open();
      return;
    }
    if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    this.open();
    let buttons = [...this.list.querySelectorAll<HTMLButtonElement>('button[data-id]:not(:disabled)')];
    if (event.key === 'Home' || event.key === 'End') {
      this.start = event.key === 'Home' ? 0 : Number.MAX_SAFE_INTEGER;
      this.draw();
      buttons = [...this.list.querySelectorAll<HTMLButtonElement>('button[data-id]:not(:disabled)')];
      (event.key === 'Home' ? buttons[0] : buttons.at(-1))?.focus();
      return;
    }
    const index = buttons.indexOf(this.root.activeElement as HTMLButtonElement);
    if (
      index >= 0 &&
      ((event.key === 'ArrowDown' && index === buttons.length - 1) ||
        (event.key === 'ArrowUp' && index === 0))
    ) {
      const direction = event.key === 'ArrowDown' ? 1 : -1;
      const page = this.list.querySelector<HTMLButtonElement>(`button[data-page="${direction}"]`);
      if (page && !page.disabled) {
        page.click();
        buttons = [...this.list.querySelectorAll<HTMLButtonElement>('button[data-id]:not(:disabled)')];
        (direction > 0 ? buttons[0] : buttons.at(-1))?.focus();
        return;
      }
    }
    const next =
      event.key === 'Home'
        ? 0
        : event.key === 'End'
          ? buttons.length - 1
          : index < 0
            ? Math.max(
                0,
                buttons.findIndex((button) => button.getAttribute('aria-selected') === 'true')
              )
            : (index + (event.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length;
    buttons[next]?.focus();
  }

  private choose(id: string, source: 'pointer' | 'keyboard'): void {
    const selected = this.selection();
    const value = this.values.multiple
      ? selected.includes(id)
        ? selected.filter((item) => item !== id)
        : [...selected.filter(Boolean), id]
      : id;
    this.removeAttribute('aria-invalid');
    this.setProperty('value', value);
    this.change(value, source);
    if (!this.values.multiple) this.shut();
    this.field.focus();
  }
}
