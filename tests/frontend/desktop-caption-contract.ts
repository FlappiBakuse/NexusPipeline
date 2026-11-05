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
  const originalOffset = document.documentElement.style.getPropertyValue('--nx-desktop-offset');
  document.documentElement.style.setProperty('--nx-desktop-offset', '40px');
  try {
    caption.render({ ready: false, maximized: false, connection: '', labels });
    check(caption.element.getBoundingClientRect().height === 40 && buttons.length === 3,
      'the one independently drawn desktop caption retains its 40px strip and three native-equivalent actions');
    buttons[0]!.click();
    check(actions.length === 0 && buttons.every(button => button.disabled),
      'caption actions stay unavailable until the bound native session is ready');
    caption.render({ ready: true, maximized: false, connection: '', labels });
    buttons[1]!.click();
    check(actions.length === 1 && actions[0] === 'toggleMaximize', 'caption dispatches exactly the selected window action');
    fixture.append(outer); outer.open = true;
    const dialog = outer.shadowRoot!.querySelector('dialog')!;
    check(caption.element.parentElement === dialog && caption.element.matches(':popover-open'),
      'the same caption follows the active native modal without acquiring its fade');
    let hit = document.elementFromPoint(20, 20);
    while (hit?.shadowRoot) {
      const next = hit.shadowRoot.elementFromPoint(20, 20);
      if (!next || next === hit) break;
      hit = next;
    }
    check(!!hit && caption.element.shadowRoot!.contains(hit),
      'the caption remains hit-testable above the inert document while a modal is active');
    check(dialog.querySelector('.mask')!.getBoundingClientRect().top === 40,
      'the desktop modal mask preserves the caption area');
    fixture.append(inner); inner.open = true;
    const innerDialog = inner.shadowRoot!.querySelector('dialog')!;
    check(caption.element.parentElement === innerDialog && caption.element.matches(':popover-open'),
      'a nested modal receives the one existing caption');
    inner.remove();
    check(caption.element.parentElement === dialog && caption.element.matches(':popover-open'),
      'removing the top modal restores the same caption to the underlying modal');
    outer.remove();
    check(caption.element.parentElement === home && caption.element.matches(':popover-open'),
      'closing the last modal returns the same caption to the application strip');
  } finally {
    inner.remove(); outer.remove(); stop(); caption.dispose(); home.remove();
    if (originalOffset) document.documentElement.style.setProperty('--nx-desktop-offset', originalOffset);
    else document.documentElement.style.removeProperty('--nx-desktop-offset');
  }
  check(!caption.element.isConnected, 'caption disposal releases its top layer and input listeners');
}
