import { NexusElement, elementAttributes } from './element-base.js';
import { FloatingElement } from './floating-element.js';
import { controlStyles } from './control-styles.js';
import { ModalSurface, modalStyles, closeIcon } from './modal-surface.js';

const dialogStyle =
  controlStyles +
  modalStyles +
  `
p{overflow-wrap:anywhere}
button[data-action=cancel]{background:transparent}
button[data-action=confirm]{background:var(--accent-strong,var(--nx-color-accent));border-color:var(--accent-strong,var(--nx-color-accent));color:#fff}
dialog[data-tone=danger] button[data-action=confirm]{background:var(--nx-color-danger);border-color:var(--nx-color-danger)}
dialog[data-tone=warning] button[data-action=confirm]{background:var(--warning);border-color:var(--warning)}
`;

export class ConfirmDialog extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-confirm-dialog');
  confirmLabel = '';
  private readonly dialog: HTMLDialogElement;
  private readonly modal: ModalSurface;
  constructor() {
    super(
      'nexus-confirm-dialog',
      dialogStyle,
      '<dialog aria-labelledby="title" aria-describedby="message"><div class="mask"></div><div class="panel"><header><h2 id="title"></h2><button class="close" type="button">' +
        closeIcon +
        '</button></header><nexus-scroll-area class="content"><p id="message"></p></nexus-scroll-area><footer><button autofocus type="button" data-action="cancel">Cancel</button><button type="button" data-action="confirm">Confirm</button></footer></div></dialog>'
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
    this.dialog.querySelector('[data-action="confirm"]')!.textContent = this.confirmLabel || (chinese ? '确认' : 'Confirm');
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
