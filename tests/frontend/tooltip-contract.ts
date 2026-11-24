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
    await new Promise(resolve => setTimeout(resolve, 130));
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
    check(wrapped.getBoundingClientRect().width > 0 && wrapped.getBoundingClientRect().height > 0,
      'a tooltip wrapper projects its actionable child into the rendered layout');
    wrapped.focus();
    await pause();
    check(tooltip.open && document.activeElement === wrapped,
      'a wrapped action retains keyboard focus while its painted tooltip is shown');
    tooltip.hidden = true;
    check(!tooltip.open && !panel.matches(':popover-open') && wrapped.getBoundingClientRect().width === 0,
      'hiding a tooltip wrapper hides its child and releases its visible top layer');
    tooltip.hidden = false;
    const select = document.createElement('nexus-select') as UiElement<'nexus-select'>;
    select.options = [{ id: 'one', label: 'One' }, { id: 'two', label: 'Two' }];
    const label = document.createElement('label'); label.append(select); fixture.append(label);
    tooltip.anchor = label;
    const question = tooltip.shadowRoot!.querySelector<HTMLButtonElement>('button')!;
    select.focus(); await pause();
    check(!tooltip.open && !question.hidden, 'a collapsed select has a separate help trigger');
    question.click();
    check(tooltip.open, 'the question mark opens help for the select');
    const input = select.shadowRoot!.querySelector('input')!;
    input.click();
    check(!tooltip.open && select.shadowRoot!.querySelector('.panel')!.matches(':popover-open'),
      'opening a select immediately hides its tooltip while preserving the option list');
    question.click();
    check(!tooltip.open, 'the trigger cannot reopen field help over an expanded select');
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    question.click();
    check(tooltip.open, 'field help is available again after the select closes');
    label.remove();
  } finally { tooltip.remove(); anchor.remove(); replacement.remove(); }
  await movingAnchorContract(fixture, check);
}

async function movingAnchorContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const clip = document.createElement('div'), wrapper = document.createElement('div'), anchor = document.createElement('button');
  const tooltip = document.createElement('nexus-tooltip') as UiElement<'nexus-tooltip'>;
  clip.style.cssText = 'height:100px;overflow:hidden';
  anchor.textContent = 'Moving anchor';
  wrapper.append(anchor); clip.append(wrapper); fixture.prepend(clip, tooltip);
  tooltip.anchor = anchor; tooltip.content = 'Following help';
  clip.scrollIntoView({ block: 'start' });
  const panel = tooltip.shadowRoot!.querySelector<HTMLElement>('.panel')!;
  let wrapperReads = 0, clipReads = 0;
  const wrapperRect = wrapper.getBoundingClientRect.bind(wrapper), clipRect = clip.getBoundingClientRect.bind(clip);
  wrapper.getBoundingClientRect = () => { wrapperReads++; return wrapperRect(); };
  clip.getBoundingClientRect = () => { clipReads++; return clipRect(); };
  const frame = () => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
  try {
    tooltip.open = true;
    await frame();
    check(tooltip.open && panel.matches(':popover-open'), 'tooltip remains open inside a visible clipping ancestor');
    const start = panel.style.top;
    wrapper.style.transform = 'translateY(16px)';
    await frame();
    check(panel.style.top !== start, 'moving an unclipped wrapper still repositions the tooltip');
    check(wrapperReads === 0 && clipReads > 0, `tooltip measures only clipping ancestors (${wrapperReads}/${clipReads})`);
    wrapper.style.transform = 'translateY(200px)';
    await frame();
    check(!tooltip.open, 'a moving anchor closes the tooltip when it leaves its clipping ancestor');
  } finally { tooltip.remove(); clip.remove(); }
}
