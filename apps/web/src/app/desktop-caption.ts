type CaptionState = {
  ready: boolean;
  maximized: boolean;
  connection: string;
  labels: { controls: string; minimize: string; resize: string; close: string };
};
type CaptionAction = 'minimize' | 'toggleMaximize' | 'close';
const maximizeIcon = '<path d="M.5.5h9v9h-9z"/>';
const restoreIcon = '<path d="M2.5 2.5v-2h7v7h-2M.5 2.5h7v7h-7z"/>';
const styles = `
:host {
  position: fixed;
  inset: 0 0 auto;
  width: 100vw;
  height: 40px;
  margin: 0;
  padding: 0;
  border: 0;
  background: var(--panel-opaque);
  color: var(--text);
  overflow: visible;
  font: inherit;
}
:host::backdrop { display: none; pointer-events: none; }
.caption {
  display: flex;
  align-items: center;
  gap: 24px;
  box-sizing: border-box;
  height: 40px;
  padding: 0 150px 0 18px;
  user-select: none;
  -webkit-app-region: drag;
}
.connection { font-size: 12px; color: var(--muted); }
.controls {
  position: absolute;
  inset: 0 0 0 auto;
  display: grid;
  grid-template-columns: 46px 46px 45px;
  -webkit-app-region: no-drag;
}
button {
  display: grid;
  place-items: center;
  box-sizing: border-box;
  height: 40px;
  padding: 0;
  border: 0;
  border-radius: 0;
  background: transparent;
  color: inherit;
  cursor: default;
}
button:hover { background: var(--hover-bg); }
button:active { background: color-mix(in srgb, var(--text) 14%, transparent); }
button:disabled { opacity: .5; }
.caption-close:hover { color: white; background: #c42b1c; }
.caption-close:active { color: white; background: #a32419; }
svg { width: 10px; height: 10px; fill: none; stroke: currentColor; stroke-width: 1; pointer-events: none; }
`;

export class DesktopCaption {
  readonly element = document.createElement('div');
  private readonly root = this.element.attachShadow({ mode: 'open' });
  private readonly owner = new AbortController();

  constructor(
    private readonly home: HTMLElement,
    action: (value: CaptionAction) => void
  ) {
    this.element.className = 'desktop-titlebar';
    this.element.style.font = getComputedStyle(document.body).font;
    this.element.popover = 'manual';
    this.root.innerHTML = `<style>${styles}</style><div class="caption"><span>NexusPipeline</span>
      <span class="connection" role="status"></span><div class="controls" role="group">
      <button type="button" tabindex="-1" data-action="minimize"><svg viewBox="0 0 10 10" aria-hidden="true"><path d="M0 5h10"/></svg></button>
      <button type="button" tabindex="-1" data-action="toggleMaximize"><svg viewBox="0 0 10 10" aria-hidden="true">${maximizeIcon}</svg></button>
      <button type="button" tabindex="-1" data-action="close" class="caption-close"><svg viewBox="0 0 10 10" aria-hidden="true"><path d="m.5.5 9 9m0-9-9 9"/></svg></button>
      </div></div>`;
    this.root.addEventListener(
      'click',
      (event) => {
        const button = (event.target as Element).closest<HTMLButtonElement>('button[data-action]');
        if (button && !button.disabled) action(button.dataset.action as CaptionAction);
      },
      { signal: this.owner.signal }
    );
    for (const button of this.root.querySelectorAll('button')) {
      const tooltip = document.createElement('nexus-tooltip') as UiElement<'nexus-tooltip'>;
      tooltip.style.cssText = 'position:absolute;width:0;height:0';
      tooltip.anchor = button;
      button.append(tooltip);
    }
    this.move();
  }

  render(state: CaptionState): void {
    this.root.querySelector('.connection')!.textContent = state.connection;
    this.root.querySelector('.controls')!.setAttribute('aria-label', state.labels.controls);
    const labels = {
      minimize: state.labels.minimize,
      toggleMaximize: state.labels.resize,
      close: state.labels.close
    };
    for (const button of this.root.querySelectorAll<HTMLButtonElement>('button')) {
      const label = labels[button.dataset.action as CaptionAction];
      button.disabled = !state.ready;
      button.setAttribute('aria-label', label);
      (button.querySelector('nexus-tooltip') as UiElement<'nexus-tooltip'>).content = label;
    }
    this.root.querySelector('[data-action=toggleMaximize] svg')!.innerHTML = state.maximized
      ? restoreIcon
      : maximizeIcon;
  }

  move(dialog?: HTMLDialogElement): void {
    const target = dialog || this.home;
    if (this.element.parentElement === target && this.element.matches(':popover-open')) return;
    if (this.element.matches(':popover-open')) this.element.hidePopover();
    target.append(this.element);
    // A modal makes other DOM inert; a descendant popover keeps this one caption active and outside its fade.
    this.element.showPopover();
  }

  dispose(): void {
    this.owner.abort();
    if (this.element.matches(':popover-open')) this.element.hidePopover();
    this.element.remove();
  }
}
import type { UiElement } from '@nexuspipeline/extension-sdk';
