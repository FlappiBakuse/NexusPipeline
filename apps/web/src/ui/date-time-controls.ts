import { createIdentity } from '../control/identity';
import { controlStyles } from './control-styles.js';
import dropdownMotion from './dropdown-motion.css?inline';
import { orientDropdown } from './dropdown-origin.js';
import { observeLocale } from './locale-observer.js';

export const pad = (value: number): string => String(value).padStart(2, '0');
export const dateText = (value: Date): string =>
  `${value.getUTCFullYear()}-${pad(value.getUTCMonth() + 1)}-${pad(value.getUTCDate())}`;
export function calendarChoices(
  month: Date,
  selected: string,
  chinese: boolean,
  allowed: (value: string) => boolean
): HTMLElement {
  const grid = document.createElement('div');
  grid.className = 'grid';
  grid.role = 'grid';
  for (const label of chinese
    ? ['日', '一', '二', '三', '四', '五', '六']
    : ['Su', 'Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa']) {
    const day = document.createElement('span');
    day.className = 'weekday';
    day.textContent = label;
    grid.append(day);
  }
  const today = new Date();
  const todayText = `${today.getFullYear()}-${pad(today.getMonth() + 1)}-${pad(today.getDate())}`;
  for (let index = 0; index < 42; index++) {
    const date = new Date(month);
    date.setUTCDate(index - month.getUTCDay() + 1);
    const value = dateText(date),
      button = choiceButton(String(date.getUTCDate()), 'date', value);
    button.className =
      'day' +
      (date.getUTCMonth() !== month.getUTCMonth() ? ' outside' : '') +
      (value === todayText ? ' today' : '');
    button.role = 'gridcell';
    button.setAttribute('aria-label', value);
    button.setAttribute('aria-selected', String(value === selected));
    button.disabled = !allowed(value);
    grid.append(button);
  }
  return grid;
}
export function choiceButton(label: string, action: string, value = ''): HTMLButtonElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = label;
  button.dataset.action = action;
  button.dataset.value = value;
  return button;
}

export type DateControlOptions = {
  value: string;
  label: string;
  min?: string;
  max?: string;
  disabled?: boolean;
};
const dateStyles =
  controlStyles +
  `
:host{display:inline-block;min-width:0}.date-control{position:relative;display:flex;width:100%}
.date-control input{appearance:none;width:100%;height:40px;min-width:0;padding:0 40px 0 12px;background:var(--content-embedded-fill,var(--content-control,var(--nx-color-surface,#1a2940)));border:1px solid var(--nx-color-border,#394152);border-radius:var(--nx-radius-sm);font:inherit;font-weight:400;color:inherit;box-shadow: var(--nx-surface-shadow);}
.date-control input[aria-invalid=true]{border-color:var(--nx-color-danger,#ff727b)}.trigger{position:absolute;right:1px;top:1px;bottom:1px;width:36px;min-height:0;padding:8px;border:0;background:transparent;color:var(--nx-color-accent);box-shadow: none;}
svg{width:16px;height:16px;fill:none;stroke:currentColor;stroke-width:1.5}
.popup{position:fixed;inset:auto;margin:0;padding:14px;width:290px;max-width:calc(100vw - 16px);border:1px solid var(--nx-color-border,#394152);border-radius:var(--nx-radius-md);background:var(--nx-color-surface,#131f33);color:var(--nx-color-text,#edf5ff);box-shadow:var(--nx-surface-shadow)}
.popup[hidden]{display:none}.popup::backdrop{background:transparent}.heading{display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;gap:4px}
.heading button{min-height:28px;padding:0 8px;background:transparent;border:0;box-shadow: none;}.heading strong{flex:1;text-align:center;font-size:13px}
.grid{display:grid;grid-template-columns:repeat(7,minmax(0,1fr));gap:3px}.weekday{text-align:center;color:var(--nx-color-muted,#92a8c4);font-size:11px;line-height:28px}
.day{min-height:30px;padding:0;border:0;background:transparent;text-align:center;font-size:12px;border-radius:var(--nx-radius-compact);box-shadow: none;}.day.outside{color:var(--faint,var(--nx-color-muted,#92a8c4))}.day[aria-selected=true]{background:var(--accent-strong,var(--nx-color-accent,#62a0ff));color:white}.day.today{box-shadow:inset 0 0 0 1px var(--nx-color-accent,#62a0ff)}
.footer{display:flex;justify-content:space-between;border-top:1px solid var(--nx-color-border,#394152);margin-top:10px;padding-top:10px;gap:8px}.footer button{min-height:28px;border:0;background:transparent;color:var(--nx-color-accent,#62a0ff);padding:0 8px;font-size:12px;box-shadow: none;}
.error{display:block;color:var(--nx-color-danger,#ff727b);font-size:11px;margin-top:4px}.error:empty{display:none}
`;

export class DateControl {
  readonly field: HTMLInputElement;
  private readonly trigger: HTMLButtonElement;
  private readonly popup: HTMLElement;
  private readonly error: HTMLElement;
  private readonly scope = new AbortController();
  private options: DateControlOptions = { value: '', label: '' };
  private month = new Date();

  constructor(
    private readonly root: ShadowRoot,
    private readonly changed: (value: string) => void
  ) {
    root.innerHTML = `<style>${dateStyles}${dropdownMotion}</style><div class="date-control"><input type="text" autocomplete="off"><button class="trigger" type="button"><svg viewBox="0 0 24 24"><rect x="4" y="5" width="16" height="16" rx="2"/><path d="M8 3v4m8-4v4M4 10h16"/></svg></button></div><span class="error" role="alert"></span><div class="popup" data-dropdown-panel role="dialog" popover="manual" hidden></div>`;
    this.field = root.querySelector('input')!;
    this.trigger = root.querySelector('button')!;
    this.popup = root.querySelector('.popup')!;
    this.error = root.querySelector('.error')!;
    this.popup.id = 'date-' + createIdentity();
    this.field.setAttribute('aria-controls', this.popup.id);
    this.field.setAttribute('aria-haspopup', 'dialog');
    const signal = this.scope.signal;
    observeLocale(root.ownerDocument, signal, () => {
      this.set(this.options);
      if (this.field.getAttribute('aria-invalid') === 'true') this.error.textContent = this.chinese
        ? '请输入有效且在允许范围内的日期或时间。'
        : 'Enter a valid date or time within the allowed range.';
      if (!this.popup.hidden) this.draw();
    });
    this.trigger.addEventListener('click', () => (this.popup.hidden ? this.open() : this.close()), {
      signal
    });
    this.field.addEventListener(
      'change',
      (event) => {
        event.stopPropagation();
        this.commit(this.field.value);
      },
      { signal }
    );
    this.field.addEventListener(
      'keydown',
      (event) => {
        if (event.key === 'ArrowDown') {
          event.preventDefault();
          this.open();
        }
        if (event.key === 'Enter') {
          event.preventDefault();
          this.commit(this.field.value);
        }
      },
      { signal }
    );
    this.popup.addEventListener('click', (event) => this.click(event), { signal });
    this.popup.addEventListener('keydown', (event) => this.key(event), { signal });
    root.addEventListener(
      'keydown',
      (event) => {
        const key = (event as KeyboardEvent).key;
        if (key === 'Escape' && !this.popup.hidden) {
          event.preventDefault();
          event.stopPropagation();
          this.close();
          this.field.focus();
        }
        if (key === 'Tab' && !this.popup.hidden) {
          this.close();
          this.field.focus();
        }
      },
      { signal }
    );
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
    window.addEventListener('resize', () => this.close(), { signal });
    window.addEventListener('hashchange', () => this.close(), { signal });
    window.addEventListener(
      'scroll',
      (event) => {
        if (!event.composedPath().includes(this.popup)) this.close();
      },
      { signal, capture: true }
    );
  }

  set(options: DateControlOptions): void {
    if (options.value !== this.options.value)
      this.field.value = options.value;
    this.options = options;
    this.field.placeholder = 'YYYY-MM-DD';
    this.field.setAttribute('aria-label', options.label);
    this.field.inputMode = 'numeric';
    this.field.disabled = this.trigger.disabled = Boolean(options.disabled);
    this.trigger.setAttribute('aria-label', options.label + (this.chinese ? ' 选择' : ' Choose'));
    this.popup.setAttribute('aria-label', options.label);
    if (options.disabled) this.close();
  }

  dispose(): void {
    this.close();
    this.scope.abort();
  }
  private get chinese(): boolean {
    return document.documentElement.lang.startsWith('zh');
  }
  private allowed(value: string): boolean {
    return (
      (!this.options.min || value >= this.options.min) && (!this.options.max || value <= this.options.max)
    );
  }
  private valid(value: string): boolean {
    if (!value) return true;
    return /^\d{4}-\d{2}-\d{2}$/.test(value) &&
      !Number.isNaN(Date.parse(value)) &&
      dateText(new Date(value + 'T00:00:00Z')) === value;
  }
  private commit(value: string): void {
    if (!this.valid(value) || (value && !this.allowed(value))) {
      this.field.setAttribute('aria-invalid', 'true');
      this.error.textContent = this.chinese
        ? '请输入有效且在允许范围内的日期或时间。'
        : 'Enter a valid date or time within the allowed range.';
      return;
    }
    this.field.setAttribute('aria-invalid', 'false');
    this.error.textContent = '';
    this.options = { ...this.options, value };
    this.field.value = value;
    this.close();
    this.changed(value);
  }
  private open(): void {
    if (this.options.disabled) return;
    const value = this.options.value;
    const today = new Date();
    this.month =
      value
        ? new Date(value + 'T00:00:00Z')
        : new Date(Date.UTC(today.getFullYear(), today.getMonth(), 1));
    this.month.setUTCDate(1);
    this.draw();
    this.popup.hidden = false;
    this.popup.showPopover();
    this.field.setAttribute('aria-expanded', 'true');
    const rect = this.root.host.getBoundingClientRect();
    this.popup.style.left = Math.max(8, Math.min(rect.left, innerWidth - this.popup.offsetWidth - 8)) + 'px';
    this.popup.style.top =
      Math.max(
        8,
        rect.bottom + this.popup.offsetHeight + 8 <= innerHeight
          ? rect.bottom + 5
          : rect.top - this.popup.offsetHeight - 5
      ) + 'px';
    orientDropdown(this.popup, this.trigger.getBoundingClientRect());
    const selected = this.popup.querySelector<HTMLButtonElement>('[aria-selected=true]:not(:disabled)');
    (selected || this.popup.querySelector<HTMLButtonElement>('button:not(:disabled)'))?.focus();
  }
  private close(): void {
    if (this.popup.matches(':popover-open')) this.popup.hidePopover();
    this.popup.hidden = true;
    this.field.setAttribute('aria-expanded', 'false');
  }
  private draw(): void {
    const active = this.root.activeElement as HTMLElement | null;
    const action = active?.dataset.action, value = active?.dataset.value;
    const heading = document.createElement('div');
    heading.className = 'heading';
    for (const [label, step] of [
      ['«', -12],
      ['‹', -1],
      ['', 0],
      ['›', 1],
      ['»', 12]
    ] as const) {
      if (!step) {
        const title = document.createElement('strong');
        title.textContent = new Intl.DateTimeFormat(document.documentElement.lang || 'zh-CN', {
          year: 'numeric',
          month: 'long',
          timeZone: 'UTC'
        }).format(this.month);
        heading.append(title);
      } else {
        const button = choiceButton(label, 'month', String(step));
        button.setAttribute(
          'aria-label',
          (step < 0 ? (this.chinese ? '上一' : 'Previous ') : this.chinese ? '下一' : 'Next ') +
            (Math.abs(step) === 12 ? (this.chinese ? '年' : 'year') : this.chinese ? '月' : 'month')
        );
        heading.append(button);
      }
    }
    this.popup.replaceChildren(
      heading,
      calendarChoices(this.month, this.options.value, this.chinese, (value) => this.allowed(value))
    );
    const footer = document.createElement('div');
    footer.className = 'footer';
    footer.append(
      choiceButton(this.chinese ? '清除' : 'Clear', 'clear'),
      choiceButton(this.chinese ? '现在' : 'Now', 'now')
    );
    this.popup.append(footer);
    if (action) Array.from(this.popup.querySelectorAll<HTMLButtonElement>('button'))
      .find(button => button.dataset.action === action && button.dataset.value === value)?.focus({ preventScroll: true });
  }
  private click(event: MouseEvent): void {
    const target = (event.target as HTMLElement).closest<HTMLButtonElement>('button');
    if (!target || target.disabled) return;
    const action = target.dataset.action,
      value = target.dataset.value!;
    if (action === 'month') {
      this.month.setUTCMonth(this.month.getUTCMonth() + Number(value));
      this.draw();
      this.popup.querySelector<HTMLButtonElement>(`[data-action=month][data-value="${value}"]`)?.focus();
    } else {
      const now = new Date();
      this.commit(
        action === 'date'
          ? value
          : action === 'clear'
            ? ''
            : `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
      );
      this.field.focus();
    }
  }
  private key(event: KeyboardEvent): void {
    const target = event.target as HTMLButtonElement;
    if (
      !target.matches('[data-action=date]') ||
      !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown'].includes(
        event.key
      )
    )
      return;
    event.preventDefault();
    const date = new Date(target.dataset.value! + 'T00:00:00Z');
    if (event.key === 'PageUp' || event.key === 'PageDown')
      date.setUTCMonth(date.getUTCMonth() + (event.key === 'PageUp' ? -1 : 1));
    else
      date.setUTCDate(
        date.getUTCDate() +
          ({
            ArrowLeft: -1,
            ArrowRight: 1,
            ArrowUp: -7,
            ArrowDown: 7,
            Home: -date.getUTCDay(),
            End: 6 - date.getUTCDay()
          }[event.key] || 0)
      );
    if (!this.allowed(dateText(date))) return;
    this.month = new Date(date);
    this.month.setUTCDate(1);
    this.draw();
    this.popup.querySelector<HTMLButtonElement>(`[data-value="${dateText(date)}"]`)?.focus();
  }
}

type TimeOptions = { value: string; label: string; disabled: boolean };
const timeStyles =
  controlStyles +
  `
:host{display:block;width:100%}.trigger{display:flex;align-items:center;justify-content:space-between;width:100%;height:40px;padding:0 12px;border-radius:var(--nx-radius-sm);background:var(--content-embedded-fill,var(--content-control));font-weight:400}
.trigger[aria-expanded=true]{border-color:var(--nx-color-accent)}.arrow{width:7px;height:7px;margin-right:4px;border:solid var(--nx-color-muted);border-width:0 1.5px 1.5px 0;transform:rotate(45deg)}
.trigger[aria-expanded=true] .arrow{transform:translateY(3px) rotate(225deg)}
.popup{position:fixed;inset:auto;margin:0;padding:12px;border:1px solid var(--content-control-border,var(--nx-color-border));border-radius:var(--nx-radius-md);background:var(--content-card);color:inherit;box-shadow:var(--nx-surface-shadow)}
.popup[hidden]{display:none}.columns{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}.wheel{display:grid;grid-template-rows:40px 120px 40px;min-width:0}
.values{display:grid;grid-template-rows:repeat(3,40px);overflow:hidden;border-block:1px solid var(--nx-color-border)}
.wheel button{display:grid;place-items:center;min-width:0;width:100%;height:40px;min-height:40px;padding:0 4px;border:0;border-radius:0;background:transparent;color:var(--nx-color-muted);font-weight:400;font-variant-numeric:tabular-nums;box-shadow: none;}
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
    root.innerHTML = `<style>${timeStyles}${dropdownMotion}</style><button class="trigger" type="button" aria-haspopup="dialog" aria-expanded="false"><span></span><i class="arrow" aria-hidden="true"></i></button><div class="popup" data-dropdown-panel role="dialog" popover="manual" hidden><div class="columns"></div></div>`;
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
    orientDropdown(this.panel, rect);
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
