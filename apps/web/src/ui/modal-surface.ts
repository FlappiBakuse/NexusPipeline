export const modalExitDuration = 200;
export const closeIcon =
  '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 6 12 12M18 6 6 18" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>';

export const modalStyles = `
:host{display:contents}
dialog{top:var(--nx-desktop-offset,0px);width:min(680px,calc(100vw - 28px));height:fit-content;max-width:none;max-height:none;padding:0;overflow:visible;
border:0;outline:0;color:var(--nx-color-text);background:transparent}
dialog[open]{display:block;animation:modal-fade var(--nx-motion-duration-fast,160ms) var(--nx-motion-ease,cubic-bezier(.2,.8,.2,1))}
.panel{display:flex;flex-direction:column;max-height:min(calc((100dvh - var(--nx-desktop-offset,0px)) * .88),900px);overflow:hidden;min-width:0;
border:1px solid var(--nx-color-border);border-radius:var(--nx-radius-lg,16px);background:var(--content-card,var(--nx-color-surface));
box-shadow:var(--shadow,0 16px 40px #0005);animation:modal-enter var(--nx-motion-duration-normal,200ms) var(--nx-motion-ease,cubic-bezier(.2,.8,.2,1))}
.mask{position:fixed;inset:var(--nx-desktop-offset,0px) 0 0;z-index:-1;background:var(--mask,rgba(3,8,16,.68));animation:modal-fade var(--nx-motion-duration-fast,160ms) var(--nx-motion-ease,cubic-bezier(.2,.8,.2,1))}
dialog[data-size=wide]{width:min(960px,calc(100vw - 28px))}
dialog[data-surface=secondary]>.panel{background:var(--content-card-secondary,var(--content-card-soft))}
dialog::backdrop{background:transparent}
header{display:flex;flex:none;align-items:center;justify-content:space-between;gap:12px;padding:clamp(18px,3vw,30px);padding-bottom:16px}
h2{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:var(--nx-modal-title-size,19px);line-height:var(--nx-modal-title-line-height,1.35);letter-spacing:-.025em;margin:0}header>.close{width:40px;min-width:40px;height:40px;padding:0;border:1px solid var(--border-strong,var(--nx-color-border));border-radius:10px;background:transparent;font-size:22px;font-weight:400;color:var(--nx-color-muted)}
header>.close{display:grid;place-items:center}header>.close svg{width:20px;height:20px;pointer-events:none}
.content{flex:1 1 auto;min-height:0;overflow:hidden;--nx-scroll-padding:0 clamp(18px,3vw,30px) 24px}
footer{display:flex;flex:none;flex-wrap:wrap;justify-content:flex-end;gap:12px;padding:16px clamp(18px,3vw,30px) clamp(18px,3vw,30px);border-top:1px solid var(--nx-color-border)}
footer[hidden]{display:none}footer button,footer ::slotted(button){min-width:80px}p{margin:0;white-space:pre-wrap;line-height:1.6}
@keyframes modal-fade{from{opacity:0}to{opacity:1}}
@keyframes modal-enter{from{opacity:0;transform:translateY(5px) scale(.985)}to{opacity:1;transform:none}}
@media(max-width:600px){:host{--nx-action-height:44px}header>.close{width:44px;min-width:44px;height:44px}}
`;

export class ModalSurface {
  private static readonly layers: HTMLDialogElement[] = [];
  private static readonly observers = new Set<(dialog?: HTMLDialogElement) => void>();
  private returnFocus: HTMLElement | null = null;
  private closing: Animation[] = [];
  private generation = 0;
  constructor(
    readonly dialog: HTMLDialogElement,
    private readonly requestClose: (reason: 'escape' | 'button' | 'backdrop') => void
  ) {}

  static watchLayer(observer: (dialog?: HTMLDialogElement) => void): () => void {
    this.observers.add(observer);
    observer(this.layers.at(-1));
    return () => {
      this.observers.delete(observer);
    };
  }

  private publishLayer(open: boolean): void {
    const index = ModalSurface.layers.indexOf(this.dialog);
    if (open === index >= 0) return;
    if (open) ModalSurface.layers.push(this.dialog);
    else ModalSurface.layers.splice(index, 1);
    for (const observer of ModalSurface.observers) observer(ModalSurface.layers.at(-1));
  }

  connect(signal: AbortSignal): void {
    // A descendant toast or nested dialog closes only its own surface.
    this.dialog.addEventListener('nexus-close', event => event.stopPropagation(), { signal });
    this.dialog.addEventListener('keydown', (event) => {
      if (event.key !== 'Tab' || ModalSurface.layers.at(-1) !== this.dialog) return;
      const targets: HTMLElement[] = [];
      const visit = (element: Element): void => {
        if (element instanceof HTMLElement && (element.hidden || element.inert ||
            element.matches(':disabled') || getComputedStyle(element).visibility === 'hidden')) return;
        if (element instanceof HTMLSlotElement) {
          for (const child of element.assignedElements({ flatten: true })) visit(child);
        } else if (element.shadowRoot) {
          for (const child of element.shadowRoot.children) visit(child);
        } else {
          if (element instanceof HTMLElement && element.tabIndex >= 0 && element.getClientRects().length)
            targets.push(element);
          for (const child of element.children) visit(child);
        }
      };
      visit(this.dialog);
      let active = this.dialog.ownerDocument.activeElement;
      while (active?.shadowRoot?.activeElement) active = active.shadowRoot.activeElement;
      const index = targets.indexOf(active as HTMLElement);
      if (!targets.length) { event.preventDefault(); this.dialog.focus(); }
      else if (event.shiftKey && index <= 0 || !event.shiftKey && (index < 0 || index === targets.length - 1)) {
        event.preventDefault();
        targets[event.shiftKey ? targets.length - 1 : 0]!.focus({ preventScroll: true });
      }
    }, { signal });
    this.dialog.addEventListener('close', () => this.publishLayer(false), { signal });
    this.dialog.addEventListener(
      'cancel',
      (event) => {
        event.preventDefault();
        this.requestClose('escape');
      },
      { signal }
    );
    this.dialog.addEventListener(
      'click',
      (event) => {
        if (event.composedPath().find((node) => node instanceof HTMLDialogElement) !== this.dialog) return;
        if ((event.target as HTMLElement).closest('.close')) {
          this.requestClose('button');
          return;
        }
        if (event.target !== this.dialog && event.target !== this.dialog.querySelector('.mask')) return;
        const rect = this.dialog.querySelector('.panel')!.getBoundingClientRect();
        if (
          event.clientX < rect.left ||
          event.clientX > rect.right ||
          event.clientY < rect.top ||
          event.clientY > rect.bottom
        )
          this.requestClose('backdrop');
      },
      { signal }
    );
  }

  show(open: boolean): void {
    if (!open) {
      this.close();
      return;
    }
    this.generation++;
    this.cancelExit();
    if (this.dialog.open) return;
    let active = document.activeElement;
    while (active?.shadowRoot?.activeElement) active = active.shadowRoot.activeElement;
    this.returnFocus = active instanceof HTMLElement ? active : null;
    this.dialog.showModal();
    this.publishLayer(true);
  }

  close(immediate = false): void {
    if (!this.dialog.open) return;
    if (immediate) {
      this.generation++;
      this.cancelExit();
      this.finish();
      return;
    }
    if (this.closing.length) return;
    if (matchMedia('(prefers-reduced-motion: reduce)').matches) {
      this.finish();
      return;
    }
    const generation = ++this.generation;
    const animation = this.dialog.querySelector('.panel')!.animate(
      [
        { opacity: 1, transform: 'none' },
        { opacity: 0, transform: 'translateY(5px) scale(.985)' }
      ],
      { duration: modalExitDuration, easing: 'cubic-bezier(.2,.8,.2,1)' }
    );
    this.closing = [
      animation,
      ...[this.dialog, this.dialog.querySelector('.mask')!].map((element) =>
        element.animate([{ opacity: 1 }, { opacity: 0 }], {
          duration: 160,
          easing: 'cubic-bezier(.2,.8,.2,1)',
          fill: 'forwards'
        })
      )
    ];
    void animation.finished
      .then(() => {
        if (this.generation === generation) {
          this.finish();
          this.cancelExit();
        }
      })
      .catch(() => {});
  }

  private cancelExit(): void {
    for (const animation of this.closing) animation.cancel();
    this.closing = [];
  }

  private finish(): void {
    this.publishLayer(false);
    this.dialog.close();
    if (this.returnFocus?.isConnected) this.returnFocus.focus({ preventScroll: true });
    this.returnFocus = null;
  }
}
