import { createIdentity } from '../control/identity';
import { controlStyles } from './control-styles.js';
import { observeLocale } from './locale-observer.js';

type TimeOptions = { value: string; label: string; disabled: boolean };
const pad = (value: number) => String(value).padStart(2, '0');
const styles =
  controlStyles +
  `
:host{display:block;width:100%}.trigger{display:flex;align-items:center;justify-content:space-between;width:100%;height:40px;padding:0 12px;border-radius:8px;background:var(--content-control);font-weight:400}
.trigger[aria-expanded=true]{border-color:var(--nx-color-accent)}.arrow{width:7px;height:7px;margin-right:4px;border:solid var(--nx-color-muted);border-width:0 1.5px 1.5px 0;transform:rotate(45deg)}
.trigger[aria-expanded=true] .arrow{transform:translateY(3px) rotate(225deg)}
.popup{position:fixed;inset:auto;margin:0;padding:12px;border:1px solid var(--content-control-border,var(--nx-color-border));border-radius:var(--nx-radius-md);background:var(--content-card);color:inherit;box-shadow:0 12px 28px #0004}
.popup[hidden]{display:none}.columns{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}.wheel{display:grid;grid-template-rows:40px 120px 40px;min-width:0}
.values{display:grid;grid-template-rows:repeat(3,40px);overflow:hidden;border-block:1px solid var(--nx-color-border)}
.wheel button{display:grid;place-items:center;min-width:0;width:100%;height:40px;min-height:40px;padding:0 4px;border:0;border-radius:0;background:transparent;color:var(--nx-color-muted);font-weight:400;font-variant-numeric:tabular-nums}
.wheel button:hover{background:var(--content-control-hover);color:var(--nx-color-text)}.step{font-size:16px}.values [aria-selected=true]{background:var(--accent-soft);color:var(--nx-color-accent);font-weight:750;box-shadow:inset 0 1px currentColor,inset 0 -1px currentColor}
`;

export class TimeControl {
  readonly field: HTMLButtonElement;
  private readonly panel: HTMLElement;
  private readonly scope = new AbortController();
  private options: TimeOptions = { value: '', label: '', disabled: false };
  private hour = 0;
  private minute = 0;

  constructor(
    private readonly root: ShadowRoot,
    private readonly changed: (value: string) => void
  ) {
    root.innerHTML = `<style>${styles}</style><button class="trigger" type="button" aria-haspopup="dialog" aria-expanded="false"><span></span><i class="arrow" aria-hidden="true"></i></button><div class="popup" role="dialog" popover="manual" hidden><div class="columns"></div></div>`;
    this.field = root.querySelector('.trigger')!;
    this.panel = root.querySelector('.popup')!;
    this.panel.id = 'time-' + createIdentity();
    this.field.setAttribute('aria-controls', this.panel.id);
    const signal = this.scope.signal;
    observeLocale(root.ownerDocument, signal, () => { if (!this.panel.hidden) this.draw(); });
    this.field.addEventListener('click', () => (this.panel.hidden ? this.open() : this.close()), { signal });
    this.panel.addEventListener(
      'click',
      (event) => {
        const button = (event.target as Element).closest<HTMLButtonElement>('button');
        if (!button) return;
        const unit = button.dataset.unit as 'hour' | 'minute';
        this.choose(
          unit,
          button.dataset.step ? this[unit] + Number(button.dataset.step) : Number(button.dataset.value)
        );
        this.panel.querySelector<HTMLButtonElement>(`[data-unit=${unit}][aria-selected=true]`)?.focus();
      },
      { signal }
    );
    root.addEventListener('keydown', (event) => this.key(event as KeyboardEvent), { signal });
    document.addEventListener(
      'pointerdown',
      (event) => {
        if (!event.composedPath().includes(root.host)) this.close();
      },
      { signal }
    );
    document.addEventListener(
      'focusin',
      (event) => {
        if (!event.composedPath().includes(root.host)) this.close();
      },
      { signal }
    );
    document.addEventListener(
      'visibilitychange',
      () => {
        if (document.hidden) this.close();
      },
      { signal }
    );
    window.addEventListener('resize', () => this.close(), { signal });
    window.addEventListener('hashchange', () => this.close(), { signal });
    window.addEventListener(
      'scroll',
      (event) => {
        if (!event.composedPath().includes(this.panel)) this.close();
      },
      { signal, capture: true }
    );
  }

  set(options: TimeOptions): void {
    this.options = options;
    [this.hour, this.minute] = (options.value || '00:00').split(':').map(Number) as [number, number];
    this.field.querySelector('span')!.textContent = `${pad(this.hour)}:${pad(this.minute)}`;
    this.field.setAttribute('aria-label', options.label);
    this.field.disabled = options.disabled;
    this.panel.setAttribute('aria-label', options.label);
    if (options.disabled) this.close();
    else if (!this.panel.hidden) this.draw();
  }

  dispose(): void {
    this.close();
    this.scope.abort();
  }

  private choose(unit: 'hour' | 'minute', value: number): void {
    if (this.options.disabled) return;
    const limit = unit === 'hour' ? 24 : 60;
    this[unit] = ((value % limit) + limit) % limit;
    const next = `${pad(this.hour)}:${pad(this.minute)}`;
    if (next !== this.options.value) this.changed(next);
    this.draw();
  }

  private open(): void {
    if (this.options.disabled) return;
    this.draw();
    this.panel.hidden = false;
    this.panel.showPopover();
    this.field.setAttribute('aria-expanded', 'true');
    const rect = this.root.host.getBoundingClientRect();
    this.panel.style.width = Math.min(rect.width, innerWidth - 16) + 'px';
    this.panel.style.left = Math.max(8, Math.min(rect.left, innerWidth - this.panel.offsetWidth - 8)) + 'px';
    this.panel.style.top =
      Math.max(
        8,
        rect.bottom + this.panel.offsetHeight + 6 <= innerHeight
          ? rect.bottom + 6
          : rect.top - this.panel.offsetHeight - 6
      ) + 'px';
  }

  private close(): void {
    if (this.panel.matches(':popover-open')) this.panel.hidePopover();
    this.panel.hidden = true;
    this.field.setAttribute('aria-expanded', 'false');
  }

  private draw(): void {
    const active = this.root.activeElement as HTMLElement | null;
    const unit = active?.dataset.unit, value = active?.dataset.value, step = active?.dataset.step;
    const columns = this.root.querySelector('.columns')!;
    columns.replaceChildren();
    const chinese = document.documentElement.lang.startsWith('zh');
    for (const unit of ['hour', 'minute'] as const) {
      const label = unit === 'hour' ? (chinese ? '小时' : 'hour') : chinese ? '分钟' : 'minute';
      const wheel = document.createElement('div');
      wheel.className = 'wheel';
      wheel.setAttribute('role', 'group');
      wheel.setAttribute('aria-label', label);
      const button = (text: string, value: number, step = false) => {
        const item = document.createElement('button');
        item.type = 'button';
        item.textContent = text;
        item.dataset.unit = unit;
        item.dataset[step ? 'step' : 'value'] = String(value);
        if (step) {
          item.className = 'step';
          item.setAttribute(
            'aria-label',
            (value < 0 ? (chinese ? '减少' : 'Decrease ') : chinese ? '增加' : 'Increase ') + label
          );
        }
        return item;
      };
      const values = document.createElement('div');
      values.className = 'values';
      values.setAttribute('role', 'listbox');
      values.setAttribute('aria-label', label);
      for (const offset of [-1, 0, 1]) {
        const limit = unit === 'hour' ? 24 : 60,
          value = (this[unit] + offset + limit) % limit;
        const item = button(pad(value), value);
        item.setAttribute('role', 'option');
        item.setAttribute('aria-selected', String(offset === 0));
        values.append(item);
      }
      wheel.append(button('⌃', -1, true), values, button('⌄', 1, true));
      columns.append(wheel);
    }
    if (unit) Array.from(columns.querySelectorAll<HTMLButtonElement>('button'))
      .find(button => button.dataset.unit === unit && button.dataset.value === value && button.dataset.step === step)
      ?.focus({ preventScroll: true });
  }

  private key(event: KeyboardEvent): void {
    if (event.key === 'Escape' && !this.panel.hidden) {
      event.preventDefault();
      event.stopPropagation();
      this.close();
      this.field.focus();
    } else if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
      event.preventDefault();
      if (this.panel.hidden) this.open();
      else
        this.choose(
          (event.target as HTMLElement).dataset.unit === 'minute' ? 'minute' : 'hour',
          ((event.target as HTMLElement).dataset.unit === 'minute' ? this.minute : this.hour) +
            (event.key === 'ArrowUp' ? -1 : 1)
        );
    } else if (event.key === 'Tab') this.close();
  }
}
