import { ModalSurface } from '../../apps/web/src/ui/modal-surface.js';
import type { UiElement } from '@nexuspipeline/extension-sdk';

export function modalOwnershipContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): void {
  const outer = document.createElement('nexus-modal') as UiElement<'nexus-modal'>;
  const inner = document.createElement('dialog');
  inner.innerHTML = '<div class="mask"></div><div class="panel"><button class="close">Close image</button></div>';
  outer.append(inner);
  fixture.append(outer);
  const lifetime = new AbortController();
  let outerClose = 0, innerClose = 0;
  outer.addEventListener('nexus-close', event => { if (event.target === outer) outerClose++; }, { signal: lifetime.signal });
  const modal = new ModalSurface(inner, () => { innerClose++; modal.close(true); });
  modal.connect(lifetime.signal);
  try {
    outer.open = true;
    modal.show(true);
    inner.querySelector<HTMLButtonElement>('.close')!.click();
    check(innerClose === 1 && outerClose === 0 && outer.open && !inner.open,
      'closing the nested image dialog preserves its containing history detail');
    modal.show(true);
    inner.querySelector<HTMLElement>('.mask')!.click();
    check(innerClose === 2 && outerClose === 0 && outer.open && !inner.open,
      'the nested backdrop closes only its own dialog');
  } finally {
    modal.close(true); lifetime.abort(); outer.remove();
  }
}
