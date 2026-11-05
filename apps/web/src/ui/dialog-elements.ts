import { NexusElement, elementAttributes } from './element-base.js';
import { FloatingElement } from './floating-element.js';
import { controlStyles } from './control-styles.js';
import { ModalSurface, modalStyles, closeIcon } from './modal-surface.js';

const dialogStyle =
  controlStyles +
  modalStyles +
  `
button[data-action=cancel]{background:transparent}
button[data-action=confirm]{background:var(--accent-strong,var(--nx-color-accent));border-color:var(--accent-strong,var(--nx-color-accent));color:#fff}
dialog[data-tone=danger] button[data-action=confirm]{background:var(--nx-color-danger);border-color:var(--nx-color-danger)}
dialog[data-tone=warning] button[data-action=confirm]{background:var(--warning);border-color:var(--warning)}
`;

export class ConfirmDialog extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-confirm-dialog');
  private readonly dialog: HTMLDialogElement;
  private readonly modal: ModalSurface;
  constructor() {
    super(
      'nexus-confirm-dialog',
      dialogStyle,
      '<dialog aria-labelledby="title"><div class="mask"></div><div class="panel"><header><h2 id="title"></h2><button class="close" type="button">' +
        closeIcon +
        '</button></header><nexus-scroll-area class="content"><p></p></nexus-scroll-area><footer><button autofocus type="button" data-action="cancel">Cancel</button><button type="button" data-action="confirm">Confirm</button></footer></div></dialog>'
    );
    this.dialog = this.root.querySelector('dialog')!;
    this.modal = new ModalSurface(this.dialog, (reason) => this.emit('nexus-close', { reason }));
  }
  protected connect(signal: AbortSignal): void {
    this.modal.connect(signal);
    this.dialog.addEventListener(
      'click',
      (event) => {
        const action = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-action]')
          ?.dataset.action;
        if (action)
          this.emit(action === 'confirm' ? 'nexus-confirm' : 'nexus-cancel', {
            actionId: action,
            source: event.detail === 0 ? 'keyboard' : 'pointer'
          });
      },
      { signal }
    );
  }
  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.modal.close(true);
  }
  override focus(options?: FocusOptions): void {
    this.dialog.querySelector<HTMLElement>('[data-action=cancel]')?.focus(options);
  }
  protected render(): void {
    const chinese = this.ownerDocument.documentElement.lang.toLowerCase().startsWith('zh');
    this.dialog.querySelector('[data-action="cancel"]')!.textContent = chinese ? '取消' : 'Cancel';
    this.dialog.querySelector('[data-action="confirm"]')!.textContent = chinese ? '确认' : 'Confirm';
    this.dialog.querySelector('.close')!.setAttribute('aria-label', chinese ? '关闭' : 'Close');
    this.dialog.querySelector('h2')!.textContent = String(this.values.title);
    this.dialog.querySelector('nexus-scroll-area')!.setAttribute('label', String(this.values.title));
    this.dialog.querySelector('p')!.textContent = String(this.values.message);
    this.dialog.dataset.tone = String(this.values.tone);
    this.modal.show(Boolean(this.values.open));
  }
}

export class DialogPopover extends FloatingElement {
  static readonly observedAttributes = elementAttributes('nexus-dialog-popover');
  constructor() {
    super(
      'nexus-dialog-popover',
      controlStyles +
        `
      .panel{z-index:100;width:480px;min-width:220px;max-width:calc(100vw - 16px);max-height:calc(100vh - 16px);overflow:hidden;
      padding:24px;border:1px solid var(--nx-color-border,#394152);border-radius:var(--nx-radius-md,10px);
      background:var(--nx-color-surface,#1e2330);outline:0}
      .panel[hidden]{display:none}.close{position:absolute;top:6px;right:6px;width:40px;min-width:40px;padding:0;background:transparent;color:var(--nx-color-muted)}.close svg{width:16px;height:16px}
    `,
      '<nexus-scroll-area class="panel" role="dialog" tabindex="-1" hidden><button class="close" type="button" aria-label="Close">' +
        closeIcon +
        '</button><slot></slot></nexus-scroll-area>',
      '.panel'
    );
  }
  protected connect(signal: AbortSignal): void {
    this.attachFloat(signal);
    this.panel.querySelector('button')!.addEventListener('click', () => this.close('button'), { signal });
  }
  override focus(options?: FocusOptions): void {
    this.panel.focus(options);
  }
  protected render(): void {
    this.panel.setAttribute('aria-label', String(this.values.label));
    this.panel.setAttribute('label', String(this.values.label));
    this.float();
  }
}

export class DragHandle extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-drag-handle');
  private readonly button: HTMLButtonElement;
  constructor() {
    super(
      'nexus-drag-handle',
      controlStyles +
        'button{min-width:var(--nx-drag-size,24px);width:var(--nx-drag-size,24px);min-height:var(--nx-drag-size,24px);height:var(--nx-drag-size,24px);padding:0;border:0;background:transparent;color:var(--nx-color-muted);cursor:grab}button:active{cursor:grabbing}',
      '<button type="button" aria-keyshortcuts="ArrowUp ArrowDown" draggable="true"><svg width="10" height="14" viewBox="0 0 10 14" aria-hidden="true"><g fill="currentColor"><circle cx="3" cy="3" r="1.2"/><circle cx="7" cy="3" r="1.2"/><circle cx="3" cy="7" r="1.2"/><circle cx="7" cy="7" r="1.2"/><circle cx="3" cy="11" r="1.2"/><circle cx="7" cy="11" r="1.2"/></g></svg></button>'
    );
    this.button = this.root.querySelector('button')!;
  }
  protected connect(signal: AbortSignal): void {
    this.button.addEventListener(
      'keydown',
      (event) => {
        if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
        event.preventDefault();
        this.emit('nexus-action', {
          actionId: `${this.values.itemId}:${event.key === 'ArrowUp' ? 'up' : 'down'}`,
          source: 'keyboard'
        });
      },
      { signal }
    );
    this.button.addEventListener(
      'dragstart',
      (event) => {
        if (this.values.disabled) {
          event.preventDefault();
          return;
        }
        event.dataTransfer?.setData('text/plain', String(this.values.itemId));
        if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
      },
      { signal }
    );
  }
  override focus(options?: FocusOptions): void {
    this.button.focus(options);
  }
  protected render(): void {
    this.button.disabled = Boolean(this.values.disabled);
    this.button.draggable = !this.values.disabled;
    this.button.setAttribute('aria-label', String(this.values.label));
  }
}

export class SortableList extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-sortable-list');
  private readonly itemSlot: HTMLSlotElement;
  private selected: string | null = null;
  constructor() {
    super('nexus-sortable-list', ':host{display:block}::slotted(*){display:block}', '<slot></slot>');
    this.itemSlot = this.root.querySelector('slot')!;
  }
  protected connect(signal: AbortSignal): void {
    this.addEventListener(
      'nexus-action',
      (event) => {
        if (this.values.disabled) return;
        const action = (event as CustomEvent<{ actionId: string }>).detail.actionId;
        const [id, direction] = action.split(':');
        if (direction !== 'up' && direction !== 'down') return;
        event.stopPropagation();
        this.move(id!, direction === 'up' ? -1 : 1, 'keyboard');
      },
      { signal }
    );
    this.addEventListener(
      'dragstart',
      (event) => {
        const target = (event.target as HTMLElement).closest('[data-item-id]');
        if (target && !this.values.disabled) this.selected = target.getAttribute('data-item-id');
      },
      { signal }
    );
    this.addEventListener(
      'dragover',
      (event) => {
        if (this.selected && !this.values.disabled) event.preventDefault();
      },
      { signal }
    );
    this.addEventListener(
      'drop',
      (event) => {
        event.preventDefault();
        const target = (event.target as HTMLElement).closest('[data-item-id]')?.getAttribute('data-item-id');
        if (target && this.selected) this.moveTo(this.selected, target);
        this.selected = null;
      },
      { signal }
    );
    this.addEventListener(
      'dragend',
      () => {
        this.selected = null;
      },
      { signal }
    );
  }
  protected render(): void {
    const ids = this.values.ids as readonly string[];
    this.setAttribute('role', 'list');
    this.setAttribute('aria-disabled', String(this.values.disabled));
    this.itemSlot.assignedElements().forEach((element) => {
      const id = element.getAttribute('data-item-id');
      element.toggleAttribute('data-sortable', !this.values.disabled && Boolean(id && ids.includes(id)));
      if (id && ids.includes(id)) element.setAttribute('role', 'listitem');
    });
  }
  private move(id: string, delta: number, source: 'keyboard'): void {
    const ids = [...(this.values.ids as string[])];
    const index = ids.indexOf(id),
      next = index + delta;
    if (index < 0 || next < 0 || next >= ids.length) return;
    [ids[index], ids[next]] = [ids[next]!, ids[index]!];
    this.setProperty('ids', ids);
    this.emit('nexus-reorder', { ids: Object.freeze(ids), source });
  }
  private moveTo(id: string, target: string): void {
    const ids = [...(this.values.ids as string[])];
    const from = ids.indexOf(id),
      to = ids.indexOf(target);
    if (from < 0 || to < 0 || from === to) return;
    ids.splice(from, 1);
    ids.splice(to, 0, id);
    this.setProperty('ids', ids);
    this.emit('nexus-reorder', { ids: Object.freeze(ids), source: 'pointer' });
  }
}
