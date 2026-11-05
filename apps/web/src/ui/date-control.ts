import { createIdentity } from '../control/identity';
import { pad, dateText, calendarChoices, timeChoices, choiceButton } from './date-choices.js';
import { controlStyles } from './control-styles.js';
import { observeLocale } from './locale-observer.js';

export type DateControlOptions = {
  value: string;
  kind: 'date' | 'time';
  label: string;
  min?: string;
  max?: string;
  disabled?: boolean;
};
const styles =
  controlStyles +
  `
:host{display:inline-block;min-width:0}.date-control{position:relative;display:flex;width:100%}
.date-control input{appearance:none;width:100%;height:40px;min-width:0;padding:0 40px 0 12px;background:var(--content-control,var(--nx-color-surface,#1a2940));border:1px solid var(--border-strong,var(--nx-color-border,#394152));border-radius:8px;font:inherit;font-weight:400;color:inherit}
.date-control input[aria-invalid=true]{border-color:var(--nx-color-danger,#ff727b)}.trigger{position:absolute;right:1px;top:1px;bottom:1px;width:36px;min-height:0;padding:8px;border:0;background:transparent;color:var(--nx-color-accent)}
svg{width:16px;height:16px;fill:none;stroke:currentColor;stroke-width:1.5}
.popup{position:fixed;inset:auto;margin:0;padding:14px;width:290px;max-width:calc(100vw - 16px);border:1px solid var(--border-strong,var(--nx-color-border,#394152));border-radius:12px;background:var(--nx-color-surface,#131f33);color:var(--nx-color-text,#edf5ff);box-shadow:0 16px 40px #0005}
.popup[hidden]{display:none}.popup::backdrop{background:transparent}.heading{display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;gap:4px}
.heading button{min-height:28px;padding:0 8px;background:transparent;border:0}.heading strong{flex:1;text-align:center;font-size:13px}
.grid{display:grid;grid-template-columns:repeat(7,minmax(0,1fr));gap:3px}.weekday{text-align:center;color:var(--nx-color-muted,#92a8c4);font-size:11px;line-height:28px}
.day{min-height:30px;padding:0;border:0;background:transparent;text-align:center;font-size:12px;border-radius:6px}.day.outside{color:var(--faint,var(--nx-color-muted,#92a8c4))}.day[aria-selected=true]{background:var(--accent-strong,var(--nx-color-accent,#62a0ff));color:white}.day.today{box-shadow:inset 0 0 0 1px var(--nx-color-accent,#62a0ff)}
.footer{display:flex;justify-content:space-between;border-top:1px solid var(--nx-color-border,#394152);margin-top:10px;padding-top:10px;gap:8px}.footer button{min-height:28px;border:0;background:transparent;color:var(--nx-color-accent,#62a0ff);padding:0 8px;font-size:12px}
.time-grid{display:grid;grid-template-columns:1fr 1fr;gap:10px}.time-column{max-height:230px;overflow:hidden}.time-items{display:grid;gap:4px}.time-column button{min-height:32px;border:0;background:transparent;text-align:center}.time-column button[aria-pressed=true]{color:var(--nx-color-accent,#62a0ff);background:var(--accent-soft,color-mix(in srgb,var(--nx-color-accent,#62a0ff) 15%,transparent))}
.error{display:block;color:var(--nx-color-danger,#ff727b);font-size:11px;margin-top:4px}.error:empty{display:none}
`;

export class DateControl {
  readonly field: HTMLInputElement;
  private readonly trigger: HTMLButtonElement;
  private readonly popup: HTMLElement;
  private readonly error: HTMLElement;
  private readonly scope = new AbortController();
  private options: DateControlOptions = { value: '', kind: 'date', label: '' };
  private month = new Date();
  private hour = 0;

  constructor(
    private readonly root: ShadowRoot,
    private readonly changed: (value: string) => void
  ) {
    root.innerHTML = `<style>${styles}</style><div class="date-control"><input type="text" autocomplete="off"><button class="trigger" type="button"><svg viewBox="0 0 24 24"><rect x="4" y="5" width="16" height="16" rx="2"/><path d="M8 3v4m8-4v4M4 10h16"/></svg></button></div><span class="error" role="alert"></span><div class="popup" role="dialog" popover="manual" hidden></div>`;
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
    this.field.placeholder = options.kind === 'time' ? 'HH:mm' : 'YYYY-MM-DD';
    this.field.setAttribute('aria-label', options.label);
    this.field.inputMode = 'numeric';
    this.field.disabled = this.trigger.disabled = Boolean(options.disabled);
    this.trigger.setAttribute('aria-label', options.label + (this.chinese ? ' 选择' : ' Choose'));
    this.popup.setAttribute('aria-label', options.label);
    this.trigger.innerHTML =
      options.kind === 'time'
        ? '<svg viewBox="0 0 24 24"><path d="m7 10 5 5 5-5"/></svg>'
        : '<svg viewBox="0 0 24 24"><rect x="4" y="5" width="16" height="16" rx="2"/><path d="M8 3v4m8-4v4M4 10h16"/></svg>';
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
    return this.options.kind === 'time'
      ? /^([01]\d|2[0-3]):[0-5]\d$/.test(value)
      : /^\d{4}-\d{2}-\d{2}$/.test(value) &&
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
      value && this.options.kind === 'date'
        ? new Date(value + 'T00:00:00Z')
        : new Date(Date.UTC(today.getFullYear(), today.getMonth(), 1));
    this.month.setUTCDate(1);
    this.hour = Number(value.split(':')[0]) || 0;
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
    this.popup.querySelector<HTMLButtonElement>('[aria-selected=true],button:not(:disabled)')?.focus();
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
    if (this.options.kind === 'time') {
      const title = document.createElement('strong');
      title.textContent = this.chinese ? '小时 / 分钟' : 'Hours / Minutes';
      heading.append(title);
    } else {
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
    }
    this.popup.replaceChildren(
      heading,
      this.options.kind === 'date'
        ? calendarChoices(this.month, this.options.value, this.chinese, (value) => this.allowed(value))
        : timeChoices(this.hour, this.options.value, this.chinese)
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
    } else if (action === 'hour') {
      this.hour = Number(value);
      this.draw();
      this.popup.querySelector<HTMLButtonElement>(`[data-action=hour][data-value="${value}"]`)?.focus();
    } else {
      const now = new Date();
      this.commit(
        action === 'date'
          ? value
          : action === 'minute'
            ? `${pad(this.hour)}:${pad(Number(value))}`
            : action === 'clear'
              ? ''
              : this.options.kind === 'time'
                ? `${pad(now.getHours())}:${pad(now.getMinutes())}`
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
