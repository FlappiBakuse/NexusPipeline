import type { UiElement } from '@nexuspipeline/extension-sdk';

export async function tooltipContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const anchor = document.createElement('button');
  const replacement = document.createElement('button');
  const tooltip = document.createElement('nexus-tooltip') as UiElement<'nexus-tooltip'>;
  anchor.textContent = 'Anchor'; replacement.textContent = 'Replacement';
  tooltip.anchor = anchor; tooltip.content = 'Painted detail'; tooltip.delay = 20;
  fixture.append(anchor, replacement, tooltip);
  const pause = () => new Promise(resolve => setTimeout(resolve, 35));
  const panel = tooltip.shadowRoot!.querySelector<HTMLElement>('.panel')!;
  try {
    anchor.dispatchEvent(new PointerEvent('pointerenter'));
    await pause();
    check(tooltip.open && panel.matches(':popover-open') && panel.textContent === 'Painted detail',
      'an external anchor opens the shared painted tooltip after its delay');
    check(document.activeElement !== panel && !anchor.title,
      'the painted tooltip neither takes keyboard focus nor needs a native title');
    anchor.dispatchEvent(new PointerEvent('pointerleave'));
    check(!tooltip.open && !panel.matches(':popover-open'), 'leaving the anchor releases the tooltip top layer');
    anchor.dispatchEvent(new PointerEvent('pointerenter'));
    tooltip.anchor = replacement;
    await pause();
    check(!tooltip.open, 'changing the anchor cancels the old delayed open');
    anchor.dispatchEvent(new PointerEvent('pointerenter'));
    await pause();
    check(!tooltip.open, 'the former anchor loses its input listeners');
    replacement.focus();
    await pause();
    const retained = tooltip.open && document.activeElement === replacement;
    check(retained, 'keyboard focus on the new anchor retains focus while showing the tooltip' + (retained ? '' :
      JSON.stringify({ open: tooltip.open, focused: document.activeElement?.outerHTML, documentFocused: document.hasFocus() })));
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    check(!tooltip.open, 'Escape closes the painted tooltip without leaving a stale open property');
    anchor.focus(); replacement.dispatchEvent(new PointerEvent('pointerenter'));
    tooltip.remove();
    await pause();
    check(!tooltip.open && !panel.matches(':popover-open'), 'removal cancels a pending tooltip and releases the top layer');
    fixture.append(tooltip);
    tooltip.open = true; tooltip.remove();
    check(!tooltip.open && !panel.matches(':popover-open'), 'removing a visible tooltip clears transient open state');
    fixture.append(tooltip);
    check(!tooltip.open && !panel.matches(':popover-open'), 'moving a caption or reconnecting an owner does not reopen a stale tooltip');
    const wrapped = document.createElement('button');
    wrapped.textContent = 'Wrapped activity action';
    tooltip.append(wrapped);
    tooltip.anchor = wrapped;
    const slot = tooltip.shadowRoot!.querySelector('slot');
    check(slot?.assignedElements().includes(wrapped) && wrapped.getBoundingClientRect().width > 0,
      'a tooltip wrapper projects its actionable child into the rendered layout');
    wrapped.focus();
    await pause();
    check(tooltip.open && document.activeElement === wrapped,
      'a wrapped action retains keyboard focus while its painted tooltip is shown');
    tooltip.hidden = true;
    check(!tooltip.open && !panel.matches(':popover-open') && wrapped.getBoundingClientRect().width === 0,
      'hiding a tooltip wrapper hides its child and releases its visible top layer');
  } finally { tooltip.remove(); anchor.remove(); replacement.remove(); }
}
