import { createIdentity } from '../control/identity';
import type { DateRange } from '@nexuspipeline/extension-sdk';
import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';
import { calendarMonth, dateKey, localDay, monthAt } from './range-calendar.js';

const styles =
  controlStyles +
  `
:host{display:block;min-width:0}.trigger{display:flex;width:100%;align-items:center;justify-content:space-between;gap:12px;padding:0 12px;border-radius:8px;background:var(--content-control);font-weight:400;text-align:left}
.trigger svg{width:16px;height:16px;flex:none;color:var(--nx-color-accent)}
.panel{position:fixed;inset:auto;margin:0;width:min(640px,calc(100vw - 32px));max-height:calc(100vh - 32px);overflow:hidden;padding:12px;
border:1px solid var(--border-strong,var(--nx-color-border));border-radius:var(--nx-radius-md,10px);background:var(--content-card,var(--nx-color-surface));color:inherit;box-shadow:var(--shadow,0 16px 40px #0005)}
.panel[hidden]{display:none}.panel::backdrop{background:transparent}.toolbar{display:grid;grid-template-columns:40px 1fr 40px;gap:8px;align-items:center;text-align:center}
.toolbar button{padding:0;font-size:20px}.months{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,298px),1fr));gap:12px}
h4{margin:12px 0 8px;font-size:12px;text-align:center}.grid{display:grid;grid-template-columns:repeat(7,minmax(0,1fr));gap:3px}
.weekday{min-height:24px;display:grid;place-items:center;color:var(--nx-color-muted);font-size:10px;font-weight:700}
.day{min-width:0;min-height:40px;padding:0;font-size:12px;border-color:transparent;background:transparent;font-weight:400;border-radius:6px}
.day.in-range{border-radius:0;background:var(--accent-soft);color:var(--nx-color-accent)}.day.start,.day.end{background:var(--nx-color-accent);color:#fff}
.day.start{border-radius:6px 0 0 6px}.day.end{border-radius:0 6px 6px 0}.day.start.end{border-radius:6px}.day:disabled{opacity:.42}
.selection{display:flex;justify-content:center;align-items:center;gap:12px;margin-top:12px;padding-top:8px;border-top:1px solid var(--nx-color-border)}
.selection span{display:grid;gap:2px;text-align:center}.selection small,.hint{color:var(--nx-color-muted);font-size:10px}.selection strong{font-size:12px}
.footer{display:flex;justify-content:space-between;align-items:center;gap:12px;margin-top:12px}.apply{background:var(--accent-strong,var(--nx-color-accent));color:#fff}
`;

const dateOf = (value: string): Date => new Date(value + 'T00:00:00');

export class DateRangePicker extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-date-range-picker');
  private readonly trigger: HTMLButtonElement;
  private readonly panel: HTMLElement;
  private expanded = false;
  private draft: DateRange = { from: null, to: null };
  private anchor: string | null = null;
  private hover: string | null = null;
  private month = new Date();
  private source: DateRange | undefined;
  private minimum: unknown;
  private maximum: unknown;
  private language: string | undefined;

  constructor() {
    super(
      'nexus-date-range-picker',
      styles,
      '<button class="trigger" type="button" aria-haspopup="dialog"><span></span><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><rect x="3" y="5" width="18" height="16" rx="2"/><path d="M7 2v6m10-6v6M3 11h18"/></svg></button><nexus-scroll-area class="panel" label="Date range" role="dialog" popover="manual" hidden><div class="toolbar"><button type="button" data-month="-1">‹</button><strong></strong><button type="button" data-month="1">›</button></div><div class="months"></div><div class="selection"></div><div class="footer"><span class="hint"></span><button class="apply" type="button"></button></div></nexus-scroll-area>'
    );
    this.trigger = this.root.querySelector('.trigger')!;
    this.panel = this.root.querySelector('.panel')!;
    this.panel.id = 'range-' + createIdentity();
    this.trigger.setAttribute('aria-controls', this.panel.id);
  }

  protected connect(signal: AbortSignal): void {
    this.trigger.addEventListener('click', () => (this.expanded ? this.shut() : this.open()), { signal });
    this.panel.addEventListener(
      'click',
      (event) => {
        const button = (event.target as HTMLElement).closest<HTMLButtonElement>('button');
        if (!button || button.disabled) return;
        if (button.dataset.month) {
          this.month = monthAt(this.month, Number(button.dataset.month));
          this.draw();
        } else if (button.dataset.date) this.choose(button.dataset.date);
        else if (button.classList.contains('apply') && this.complete()) {
          const value = Object.freeze({ ...this.draft });
          this.shut();
          this.setProperty('value', value);
          this.change(value, event.detail === 0 ? 'keyboard' : 'pointer');
        }
      },
      { signal }
    );
    this.panel.addEventListener(
      'mouseover',
      (event) => {
        const date = (event.target as HTMLElement).closest<HTMLElement>('[data-date]')?.dataset.date;
        if (this.anchor && date && this.allowed(date)) {
          this.hover = date;
          this.paintRange();
        }
      },
      { signal }
    );
    this.panel.addEventListener(
      'mouseleave',
      () => {
        this.hover = null;
        this.paintRange();
      },
      { signal }
    );
    this.root.addEventListener('keydown', (event) => this.keyboard(event as KeyboardEvent), { signal });
    document.addEventListener(
      'pointerdown',
      (event) => {
        if (!event.composedPath().includes(this)) this.shut(false);
      },
      { signal }
    );
    document.addEventListener(
      'focusin',
      (event) => {
        if (!event.composedPath().includes(this)) this.shut(false);
      },
      { signal }
    );
    document.addEventListener(
      'visibilitychange',
      () => {
        if (document.hidden) this.shut(false);
      },
      { signal }
    );
    window.addEventListener('resize', () => this.shut(false), { signal });
    window.addEventListener('hashchange', () => this.shut(false), { signal });
    window.addEventListener(
      'scroll',
      (event) => {
        if (!event.composedPath().includes(this.panel)) this.shut(false);
      },
      { signal, capture: true }
    );
  }

  override focus(options?: FocusOptions): void {
    this.trigger.focus(options);
  }
  override disconnectedCallback(): void {
    this.shut(false);
    super.disconnectedCallback();
  }
  protected render(): void {
    const value = this.values.value as DateRange;
    if (
      (this.source && (this.source.from !== value.from || this.source.to !== value.to)) ||
      this.minimum !== this.values.min ||
      this.maximum !== this.values.max
    )
      this.shut(false);
    this.source = value;
    this.minimum = this.values.min;
    this.maximum = this.values.max;
    this.trigger.querySelector('span')!.textContent = [value.from || '—', value.to || '—']
      .map((date) => date.replaceAll('-', '/'))
      .join(this.text(' 至 ', ' to '));
    this.trigger.setAttribute(
      'aria-label',
      this.getAttribute('aria-label') || this.text('选择日期范围', 'Choose date range')
    );
    this.trigger.setAttribute('aria-expanded', String(this.expanded));
    this.describe(this.trigger);
    if (this.expanded && this.language !== this.ownerDocument.documentElement.lang) this.draw();
  }

  private text(chinese: string, english: string): string {
    return this.ownerDocument.documentElement.lang.startsWith('zh') ? chinese : english;
  }
  private allowed(date: string): boolean {
    return (
      /^\d{4}-\d{2}-\d{2}$/.test(date) &&
      (!this.values.min || date >= String(this.values.min)) &&
      (!this.values.max || date <= String(this.values.max))
    );
  }
  private open(): void {
    if (this.expanded || this.closest('[inert]')) return;
    this.draft = { ...(this.values.value as DateRange) };
    this.anchor = null;
    this.hover = null;
    let first = this.draft.from || dateKey(new Date());
    if (this.values.min && first < String(this.values.min)) first = String(this.values.min);
    if (this.values.max && first > String(this.values.max)) first = String(this.values.max);
    this.month = monthAt(dateOf(first));
    this.expanded = true;
    this.language = undefined;
    this.panel.hidden = false;
    this.panel.showPopover();
    this.render();
    this.panel.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus();
  }
  private shut(focus = true): void {
    if (!this.expanded) return;
    this.expanded = false;
    this.anchor = null;
    this.hover = null;
    if (this.panel.matches(':popover-open')) this.panel.hidePopover();
    this.panel.hidden = true;
    this.trigger.setAttribute('aria-expanded', 'false');
    if (focus && this.isConnected) this.trigger.focus();
  }
  private choose(date: string): void {
    if (!this.allowed(date)) return;
    if (!this.anchor) {
      this.anchor = date;
      this.draft = { from: date, to: this.draft.to && this.draft.to < date ? date : this.draft.to };
    } else {
      this.draft = {
        from: date < this.anchor ? date : this.anchor,
        to: date > this.anchor ? date : this.anchor
      };
      this.anchor = null;
    }
    this.hover = null;
    this.paintRange();
  }
  private complete(): boolean {
    const { from, to } = this.draft;
    return !!from && !!to && from <= to && this.allowed(from) && this.allowed(to);
  }
  private paintRange(): void {
    let { from, to } = this.draft;
    if (this.anchor && this.hover) {
      from = this.hover < this.anchor ? this.hover : this.anchor;
      to = this.hover > this.anchor ? this.hover : this.anchor;
    }
    this.panel.querySelectorAll<HTMLElement>('[data-date]').forEach((button) => {
      const date = button.dataset.date!;
      button.classList.toggle('start', date === from);
      button.classList.toggle('end', date === to);
      button.classList.toggle('in-range', !!from && !!to && date > from && date < to);
      button.setAttribute('aria-pressed', String(date === from || date === to));
    });
    const selection = this.panel.querySelector('.selection')!;
    selection.replaceChildren();
    for (const [label, value] of [
      [this.text('开始', 'Start'), from],
      [this.text('结束', 'End'), to]
    ]) {
      const span = document.createElement('span'),
        small = document.createElement('small'),
        strong = document.createElement('strong');
      small.textContent = label!;
      strong.textContent = value || '—';
      span.append(small, strong);
      selection.append(span);
    }
    this.panel.querySelector<HTMLButtonElement>('.apply')!.disabled = !this.complete();
  }
  private draw(): void {
    const locale = this.ownerDocument.documentElement.lang || 'zh-CN';
    this.language = this.ownerDocument.documentElement.lang;
    const label = (date: Date) =>
      new Intl.DateTimeFormat(locale, { year: 'numeric', month: 'long' }).format(date);
    const second = monthAt(this.month, 1);
    this.panel.querySelector('.toolbar strong')!.textContent = label(this.month) + ' — ' + label(second);
    this.panel.setAttribute('aria-label', this.text('选择日期范围', 'Choose date range'));
    const controls = this.panel.querySelectorAll<HTMLButtonElement>('[data-month]');
    controls[0]!.setAttribute('aria-label', this.text('上个月', 'Previous month'));
    controls[1]!.setAttribute('aria-label', this.text('下个月', 'Next month'));
    controls[0]!.disabled =
      (this.month.getFullYear() === 0 && this.month.getMonth() === 0) ||
      (!!this.values.min &&
        dateKey(localDay(this.month.getFullYear(), this.month.getMonth(), 0)) < String(this.values.min));
    controls[1]!.disabled =
      second.getFullYear() >= 10000 ||
      (!!this.values.max && dateKey(second).slice(0, 7) >= String(this.values.max).slice(0, 7));
    this.panel
      .querySelector('.months')!
      .replaceChildren(
        ...[this.month, second].map((month) => calendarMonth(month, locale, (date) => this.allowed(date)))
      );
    this.panel.querySelector('.hint')!.textContent = this.text(
      '选择开始和结束日期。',
      'Select a start and end date.'
    );
    this.panel.querySelector('.apply')!.textContent = this.text('应用', 'Apply');
    this.paintRange();
    const rect = this.trigger.getBoundingClientRect();
    this.panel.style.left =
      Math.max(16, Math.min(rect.left, innerWidth - this.panel.offsetWidth - 16)) + 'px';
    this.panel.style.top =
      Math.max(16, Math.min(rect.bottom + 8, innerHeight - this.panel.offsetHeight - 16)) + 'px';
  }
  private keyboard(event: KeyboardEvent): void {
    if (!this.expanded && ['ArrowDown', 'Enter', ' '].includes(event.key)) {
      event.preventDefault();
      this.open();
      return;
    }
    if (!this.expanded) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.shut();
      return;
    }
    const date = (event.target as HTMLElement).dataset.date;
    const offsets: Record<string, number> = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 };
    if (!date || offsets[event.key] === undefined) return;
    event.preventDefault();
    const next = dateOf(date);
    next.setDate(next.getDate() + offsets[event.key]!);
    const value = dateKey(next);
    if (!this.allowed(value)) return;
    if (!this.panel.querySelector(`[data-date="${value}"]`)) {
      this.month = monthAt(next);
      this.draw();
    }
    this.panel.querySelector<HTMLButtonElement>(`[data-date="${value}"]`)?.focus();
    if (this.anchor) {
      this.hover = value;
      this.paintRange();
    }
  }
}
