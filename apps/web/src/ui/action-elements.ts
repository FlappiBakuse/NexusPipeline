import type { MenuItem } from '@nexuspipeline/extension-sdk';
import { NexusElement, elementAttributes } from './element-base.js';
import { FloatingElement } from './floating-element.js';
import { controlStyles } from './control-styles.js';
import { iconPaths } from './icon-paths.js';
import dropdownMotion from './dropdown-motion.css?inline';

const panelStyle =
  controlStyles + dropdownMotion +
  `
.panel{z-index:100;min-width:160px;max-width:min(360px,calc(100vw - 16px));max-height:min(420px,calc(100vh - 16px));overflow:hidden;
padding:0;border:0;background:transparent;outline:0;
  box-shadow: none;}
.panel[hidden]{display:none}button{text-align:left}button[data-tone=danger]{color:var(--nx-color-danger,#ef7070)}
`;

export class IconButton extends NexusElement {
  static readonly observedAttributes = [...elementAttributes('nexus-icon-button'), 'aria-pressed'];
  private readonly button: HTMLButtonElement;
  constructor() {
    super(
      'nexus-icon-button',
      controlStyles +
        'button{display:flex;align-items:center;justify-content:center;width:100%;height:100%;min-width:40px;padding:0;background:transparent;border-color:var(--nx-icon-border,var(--nx-color-border))}svg{width:18px;height:18px}::slotted([slot=icon]){pointer-events:none}',
      '<button type="button"><slot name="icon"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"></svg></slot></button>'
    );
    this.button = this.root.querySelector('button')!;
  }
  protected connect(signal: AbortSignal): void {
    this.addEventListener(
      'click',
      (event) => {
        if (this.values.disabled) {
          event.preventDefault();
          event.stopImmediatePropagation();
        }
      },
      { capture: true, signal }
    );
  }
  override focus(options?: FocusOptions): void {
    this.button.focus(options);
  }
  protected render(): void {
    this.button.disabled = Boolean(this.values.disabled);
    this.button.setAttribute('aria-label', String(this.values.label));
    const pressed = this.getAttribute('aria-pressed');
    if (pressed === null) this.button.removeAttribute('aria-pressed');
    else this.button.setAttribute('aria-pressed', pressed);
    this.button.querySelector('svg')!.innerHTML = iconPaths[String(this.values.icon)] ?? iconPaths.dashboard!;
    if (this.values.expanded === null) this.button.removeAttribute('aria-expanded');
    else this.button.setAttribute('aria-expanded', String(this.values.expanded));
    this.describe(this.button);
  }
}

export class NexusMenuElement extends FloatingElement {
  static readonly observedAttributes = elementAttributes('nexus-menu');
  constructor() {
    super(
      'nexus-menu',
      panelStyle,
      '<slot name="trigger"></slot><nexus-scroll-area class="panel" data-dropdown-panel label="Menu" role="menu" tabindex="-1" hidden></nexus-scroll-area>',
      '.panel'
    );
  }
  protected connect(signal: AbortSignal): void {
    this.attachFloat(signal);
    this.panel.addEventListener(
      'click',
      (event) => {
        const button = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-id]');
        if (button && !button.disabled)
          this.emit('nexus-action', {
            actionId: button.dataset.id!,
            source: event.detail === 0 ? 'keyboard' : 'pointer'
          });
      },
      { signal }
    );
    this.panel.addEventListener(
      'keydown',
      (event) => {
        if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        const buttons = [...this.panel.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')];
        const current = buttons.indexOf(this.root.activeElement as HTMLButtonElement);
        const next =
          event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? buttons.length - 1
              : (current + (event.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length;
        buttons[next]?.focus();
      },
      { signal }
    );
  }
  override focus(options?: FocusOptions): void {
    this.panel.querySelector<HTMLElement>('button:not(:disabled)')?.focus(options);
  }
  protected render(): void {
    this.panel.replaceChildren(
      ...(this.values.items as readonly MenuItem[]).flatMap((item) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.role = 'menuitem';
        button.dataset.id = item.id;
        button.dataset.tone = item.tone ?? 'neutral';
        button.disabled = Boolean(item.disabled);
        button.textContent = item.label;
        return item.separatorBefore ? [document.createElement('hr'), button] : [button];
      })
    );
    this.float();
  }
}

export { NexusSelectElement } from './select-element.js';

export { Pager } from './pager-element.js';

export class NexusButtonElement extends NexusElement {
  static readonly formAssociated = true;
  static readonly observedAttributes = [...elementAttributes('nexus-button'), 'aria-pressed'];
  private readonly button: HTMLButtonElement;
  private readonly fallback: Text;
  private readonly internals: ElementInternals;

  constructor() {
    super(
      'nexus-button',
      controlStyles +
        `button{width:100%;min-width:0;padding:var(--nx-button-padding,0 15px);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;background:var(--nx-button-fill,transparent)}
      button[data-tone=danger]{background:var(--bad-soft,transparent)}
    `,
      '<button><slot name="icon"></slot><slot></slot></button>'
    );
    this.button = this.root.querySelector('button')!;
    this.internals = this.attachInternals();
    this.fallback = document.createTextNode('');
    this.root.querySelector('slot:not([name])')!.append(this.fallback);
  }

  protected connect(signal: AbortSignal): void {
    this.addEventListener(
      'click',
      (event) => {
        if (this.values.disabled || this.values.busy) {
          event.preventDefault();
          event.stopImmediatePropagation();
        }
      },
      { capture: true, signal }
    );
    this.button.addEventListener(
      'click',
      (event) => {
        queueMicrotask(() => {
          if (event.defaultPrevented || this.values.disabled || this.values.busy || signal.aborted) return;
          if (this.values.type === 'submit') this.internals.form?.requestSubmit();
          if (this.values.type === 'reset') this.internals.form?.reset();
        });
      },
      { signal }
    );
  }

  override focus(options?: FocusOptions): void {
    this.button.focus(options);
  }

  protected render(): void {
    this.button.type = 'button';
    this.button.disabled = Boolean(this.values.disabled || this.values.busy);
    this.button.setAttribute('aria-busy', String(this.values.busy));
    this.button.dataset.tone = String(this.values.tone);
    this.fallback.data = String(this.values.label);
    const name = this.getAttribute('aria-label');
    if (name !== null) this.button.setAttribute('aria-label', name);
    else this.button.removeAttribute('aria-label');
    const pressed = this.getAttribute('aria-pressed');
    if (pressed === null) this.button.removeAttribute('aria-pressed');
    else this.button.setAttribute('aria-pressed', pressed);
    this.describe(this.button);
  }
}
