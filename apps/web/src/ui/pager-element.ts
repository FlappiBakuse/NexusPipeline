import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';

const styles =
  controlStyles +
  `
:host{display:flex;align-items:center;flex-wrap:wrap;gap:8px;width:100%;margin-top:16px;padding:12px 16px 16px;border-top:1px solid var(--nx-color-border)}
:host([hidden]){display:none}.summary{margin-right:auto;color:var(--nx-color-muted);font-size:12px;font-variant-numeric:tabular-nums}
.controls{display:inline-flex;align-items:center;flex-wrap:wrap;gap:8px}button{min-width:36px;min-height:40px;padding:0 11px;font-size:12px;border-radius:8px;background:transparent}
button[aria-current=page]{background:var(--accent-soft);border-color:var(--border-strong,var(--nx-color-border));color:var(--nx-color-accent)}
.gap{color:var(--nx-color-muted);padding:0 4px}
`;

export class Pager extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-pager');

  constructor() {
    super(
      'nexus-pager',
      styles,
      '<nav aria-label="Pagination"><span class="summary" aria-live="polite"></span><div class="controls"></div></nav>'
    );
    this.root.querySelector('nav')!.style.display = 'contents';
  }

  protected connect(signal: AbortSignal): void {
    this.root.querySelector<HTMLElement>('.controls')!.addEventListener(
      'click',
      (event) => {
        const button = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-page]');
        if (!button || button.disabled) return;
        const target = Number(button.dataset.page);
        const { page, pages } = this.bounds();
        if (target === page || target < 1 || target > (pages ?? page + Number(this.values.hasNext))) return;
        this.change(target, event.detail === 0 ? 'keyboard' : 'pointer');
      },
      { signal }
    );
  }

  override focus(options?: FocusOptions): void {
    const current = this.root.querySelector<HTMLElement>('[aria-current=page]');
    (current || this.root.querySelector<HTMLElement>('button:not(:disabled)'))?.focus(options);
  }

  protected render(): void {
    const { page, pages, size, total } = this.bounds();
    const chinese = this.ownerDocument.documentElement.lang.startsWith('zh');
    this.hidden = (pages ?? page + Number(this.values.hasNext)) <= 1;
    const hadFocus = !!this.root.activeElement;
    this.root
      .querySelector('nav')!
      .setAttribute('aria-label', this.getAttribute('aria-label') || (chinese ? '分页' : 'Pagination'));
    const summary = this.root.querySelector('.summary')!;
    if (total === null) summary.textContent = chinese ? `第 ${page} 页` : `Page ${page}`;
    else {
      const from = total ? (page - 1) * size + 1 : 0;
      const to = Math.min(total, page * size);
      summary.textContent = chinese ? `共 ${total} 条，第 ${from}-${to} 条` : `${total} items, ${from}–${to}`;
    }
    const controls = this.root.querySelector('.controls')!;
    controls.replaceChildren();
    const button = (target: number, label: string, disabled = false) => {
      const control = document.createElement('button');
      control.type = 'button';
      control.dataset.page = String(target);
      control.textContent = label;
      control.disabled = disabled;
      return control;
    };
    controls.append(button(page - 1, chinese ? '上一页' : 'Previous', page <= 1));
    const last = pages ?? page + Number(this.values.hasNext);
    const numbers = new Set([1, last]);
    const first = Math.max(1, Math.min(page - 2, last - 4));
    for (let offset = 0; offset < 5 && first + offset <= last; offset++) numbers.add(first + offset);
    let previous = 0;
    for (const number of [...numbers].sort((a, b) => a - b)) {
      if (previous && number - previous > 1) {
        const gap = document.createElement('span');
        gap.className = 'gap';
        gap.textContent = '…';
        controls.append(gap);
      }
      const control = button(number, String(number));
      if (number === page) control.setAttribute('aria-current', 'page');
      controls.append(control);
      previous = number;
    }
    controls.append(
      button(page + 1, chinese ? '下一页' : 'Next', pages === null ? !this.values.hasNext : page >= pages)
    );
    if (hadFocus) this.focus({ preventScroll: true });
  }

  private bounds(): { page: number; size: number; total: number | null; pages: number | null } {
    const size = Math.max(1, Math.min(Number.MAX_SAFE_INTEGER, Math.floor(Number(this.values.pageSize))));
    const total =
      this.values.total === null
        ? null
        : Math.max(0, Math.min(Number.MAX_SAFE_INTEGER, Math.floor(Number(this.values.total))));
    const pages = total === null ? null : Math.max(1, Math.ceil(total / size));
    const page = Math.max(
      1,
      Math.min(pages ?? Number.MAX_SAFE_INTEGER, Math.floor(Number(this.values.page)))
    );
    return { page, size, total, pages };
  }
}
