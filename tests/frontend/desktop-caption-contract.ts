import { DesktopCaption } from '../../apps/web/src/app/desktop-caption.js';
import { ModalSurface } from '../../apps/web/src/ui/modal-surface.js';
import type { UiElement } from '@nexuspipeline/extension-sdk';

export function desktopCaptionContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): void {
  const home = document.createElement('div'); fixture.append(home);
  const actions: string[] = [];
  const labels = { controls: 'Window controls', minimize: 'Minimize', resize: 'Maximize', close: 'Close' };
  const caption = new DesktopCaption(home, action => actions.push(action));
  const stop = ModalSurface.watchLayer(dialog => caption.move(dialog));
  const buttons = [...caption.element.shadowRoot!.querySelectorAll('button')];
  const outer = document.createElement('nexus-modal') as UiElement<'nexus-modal'>;
  const inner = document.createElement('nexus-modal') as UiElement<'nexus-modal'>;
  function usableCaption(state: string): void {
    const button = caption.element.shadowRoot!.querySelector<HTMLButtonElement>('[aria-label=Minimize]')!;
    const bounds = button.getBoundingClientRect();
    const x = bounds.left + bounds.width / 2, y = bounds.top + bounds.height / 2;
    let hit = document.elementFromPoint(x, y);
    while (hit?.shadowRoot) {
      const next = hit.shadowRoot.elementFromPoint(x, y);
      if (!next || next === hit) break;
      hit = next;
    }
    check(!!hit && (hit === button || button.contains(hit)),
      'window action remains hit-testable with ' + state);
    const before = actions.length;
    (hit as HTMLElement).click();
    check(actions.length === before + 1 && actions.at(-1) === 'minimize',
      'window action dispatches once with ' + state);
  }
  const originalOffset = document.documentElement.style.getPropertyValue('--nx-desktop-offset');
  document.documentElement.style.setProperty('--nx-desktop-offset', '40px');
  try {
    caption.render({ ready: false, maximized: false, connection: '', labels });
    check(caption.element.getBoundingClientRect().height > 0
      && ['Minimize', 'Maximize', 'Close'].every(label => buttons.some(button => button.getAttribute('aria-label') === label)),
      'the visible desktop caption exposes named minimize, maximize and close actions');
    buttons[0]!.click();
    check(actions.length === 0 && buttons.every(button => button.disabled),
      'caption actions stay unavailable until the bound native session is ready');
    caption.render({ ready: true, maximized: false, connection: '', labels });
    buttons[1]!.click();
    check(actions.length === 1 && actions[0] === 'toggleMaximize', 'caption dispatches exactly the selected window action');
    fixture.append(outer); outer.open = true;
    const dialog = outer.shadowRoot!.querySelector('dialog')!;
    usableCaption('an active modal');
    check(dialog.querySelector('.mask')!.getBoundingClientRect().top >= caption.element.getBoundingClientRect().bottom,
      'the desktop modal mask preserves the caption area');
    fixture.append(inner); inner.open = true;
    usableCaption('nested modals');
    inner.remove();
    usableCaption('the remaining outer modal');
    outer.remove();
    usableCaption('the application after all modals close');
  } finally {
    inner.remove(); outer.remove(); stop(); caption.dispose(); home.remove();
    if (originalOffset) document.documentElement.style.setProperty('--nx-desktop-offset', originalOffset);
    else document.documentElement.style.removeProperty('--nx-desktop-offset');
  }
  check(!caption.element.isConnected, 'caption disposal releases its top layer and input listeners');
}
