import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';
import { ModalSurface, modalStyles, closeIcon } from './modal-surface.js';

const styles = controlStyles + modalStyles;

export class NexusModalElement extends NexusElement {
  static readonly observedAttributes = [...elementAttributes('nexus-modal'), 'hidden'];
  private readonly dialog: HTMLDialogElement;
  private readonly heading: HTMLElement;
  private readonly closeButton: HTMLButtonElement;
  private readonly modal: ModalSurface;

  constructor() {
    super(
      'nexus-modal',
      styles,
      '<dialog aria-labelledby="title"><div class="mask"></div><div class="panel"><header><h2 id="title"></h2><slot name="header-actions"></slot><button class="close" type="button" aria-label="Close">' +
        closeIcon +
        '</button></header><nexus-scroll-area class="content"><slot></slot></nexus-scroll-area><footer><slot name="footer"></slot></footer></div></dialog>'
    );
    this.dialog = this.root.querySelector('dialog')!;
    this.heading = this.root.querySelector('h2')!;
    this.closeButton = this.root.querySelector('button')!;
    this.modal = new ModalSurface(this.dialog, (reason) => this.requestClose(reason));
  }

  protected connect(signal: AbortSignal): void {
    this.root
      .querySelector('slot[name=footer]')!
      .addEventListener('slotchange', () => this.render(), { signal });
    this.modal.connect(signal);
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.modal.close(true);
  }

  override focus(options?: FocusOptions): void {
    this.dialog.focus(options);
  }

  protected render(): void {
    this.heading.textContent = String(this.values.title);
    this.root.querySelector('nexus-scroll-area')!.setAttribute('label', String(this.values.title));
    this.root.querySelector('footer')!.hidden = !(
      this.root.querySelector('slot[name=footer]') as HTMLSlotElement
    ).assignedNodes().length;
    this.closeButton.setAttribute(
      'aria-label',
      this.ownerDocument.documentElement.lang.startsWith('zh') ? '关闭' : 'Close'
    );
    this.dialog.dataset.size = String(this.values.size);
    this.dialog.dataset.surface = String(this.values.surface);
    this.closeButton.disabled = Boolean(this.values.locked);
    if (this.hidden) this.modal.close(true);
    else this.modal.show(Boolean(this.values.open));
  }

  private requestClose(reason: 'button' | 'escape' | 'backdrop'): void {
    if (this.values.locked) return;
    this.dispatchEvent(
      new CustomEvent('nexus-close', {
        bubbles: true,
        composed: true,
        detail: Object.freeze({ reason })
      })
    );
  }
}
