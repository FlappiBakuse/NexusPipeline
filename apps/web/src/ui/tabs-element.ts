import { createIdentity } from '../control/identity';
import type { TabItem } from '@nexuspipeline/extension-sdk';
import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';

export class NexusTabsElement extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-tabs');
  private readonly tablist: HTMLElement;
  private readonly panels: HTMLSlotElement;
  private readonly identity = createIdentity();

  constructor() {
    super(
      'nexus-tabs',
      controlStyles +
        `
      :host{display:block}.tabs{display:flex;flex-wrap:wrap;gap:8px}button{background:transparent}
      [aria-selected=true]{background:var(--accent-strong,var(--nx-color-accent));border-color:var(--accent-strong,var(--nx-color-accent));color:white}
    `,
      '<div class="tabs" role="tablist"></div><slot name="panels"></slot>'
    );
    this.tablist = this.root.querySelector('.tabs')!;
    this.panels = this.root.querySelector('slot')!;
  }

  protected connect(signal: AbortSignal): void {
    this.tablist.addEventListener(
      'click',
      (event) => {
        const button = (event.target as Element).closest('button');
        if (button && !button.disabled)
          this.select(button.dataset.id!, event.detail === 0 ? 'keyboard' : 'pointer');
      },
      { signal }
    );
    this.tablist.addEventListener('keydown', (event) => this.navigate(event), { signal });
    this.panels.addEventListener('slotchange', () => this.updatePanels(), { signal });
  }

  override focus(options?: FocusOptions): void {
    this.tablist.querySelector<HTMLElement>('[tabindex="0"]')?.focus(options);
  }

  protected override setProperty(key: string, value: unknown): void {
    if (key === 'items') {
      if (
        !Array.isArray(value) ||
        value.length > 128 ||
        value.some(
          (item) =>
            !item ||
            typeof item.id !== 'string' ||
            !item.id ||
            typeof item.label !== 'string' ||
            (item.disabled !== undefined && typeof item.disabled !== 'boolean')
        ) ||
        new Set(value.map((item) => item.id)).size !== value.length
      )
        throw new TypeError('Invalid tab items');
    }
    super.setProperty(key, value);
  }

  protected render(): void {
    const items = this.values.items as readonly TabItem[];
    const selected = String(this.values.selectedId) || items.find((item) => !item.disabled)?.id || '';
    const focused =
      this.root.activeElement instanceof HTMLButtonElement ? this.root.activeElement.dataset.id : undefined;
    this.tablist.replaceChildren(
      ...items.map((item, index) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.id = this.identity + '-tab-' + index;
        button.dataset.id = item.id;
        button.setAttribute('role', 'tab');
        button.setAttribute('aria-selected', String(item.id === selected));
        button.setAttribute('aria-controls', this.identity + '-panel-' + index);
        button.disabled = Boolean(item.disabled);
        button.tabIndex = !item.disabled && (item.id === selected || (!selected && index === 0)) ? 0 : -1;
        button.textContent = item.label + (item.badge ? ' ' + item.badge : '');
        return button;
      })
    );
    this.updatePanels();
    if (focused)
      [...this.tablist.querySelectorAll('button')].find((button) => button.dataset.id === focused)?.focus();
  }

  private updatePanels(): void {
    const items = this.values.items as readonly TabItem[];
    for (const panel of this.panels.assignedElements()) {
      const index = items.findIndex((item) => item.id === panel.getAttribute('data-tab-id'));
      if (!(panel instanceof HTMLElement)) continue;
      const selected = String(this.values.selectedId) || items.find((item) => !item.disabled)?.id || '';
      panel.hidden = index < 0 || items[index]?.id !== selected;
      panel.inert = panel.hidden;
      if (index < 0) continue;
      panel.id = this.identity + '-panel-' + index;
      panel.setAttribute('role', 'tabpanel');
      const tab = this.tablist.querySelectorAll('button')[index]!;
      panel.setAttribute('aria-label', items[index]!.label);
      tab.ariaControlsElements = [panel];
    }
  }

  private navigate(event: KeyboardEvent): void {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    const buttons = [...this.tablist.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')];
    if (!buttons.length) return;
    event.preventDefault();
    const current = buttons.indexOf(this.root.activeElement as HTMLButtonElement);
    const next =
      event.key === 'Home'
        ? 0
        : event.key === 'End'
          ? buttons.length - 1
          : (current + (event.key === 'ArrowRight' ? 1 : -1) + buttons.length) % buttons.length;
    const button = buttons[next]!;
    button.focus();
    this.select(button.dataset.id!, 'keyboard');
  }

  private select(id: string, source: 'pointer' | 'keyboard'): void {
    if (id === this.values.selectedId) return;
    this.setProperty('selectedId', id);
    this.change(id, source);
  }
}
