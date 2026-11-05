import type { UiElement } from '@nexuspipeline/extension-sdk';

const frame = () => new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
export async function scrollAreaContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const area = document.createElement('nexus-scroll-area') as UiElement<'nexus-scroll-area'>;
  area.direction = 'both'; area.label = 'Large record table';
  area.style.cssText = 'width:260px;height:120px';
  const content = document.createElement('div');
  content.style.cssText = 'width:900px;height:700px'; content.textContent = 'Far record';
  area.append(content); fixture.append(area);
  try {
    await frame(); await frame();
    const root = area.shadowRoot!, viewport = root.querySelector<HTMLElement>('.viewport')!;
    const vertical = root.querySelector<HTMLElement>('[data-axis=y] > .thumb')!;
    const horizontal = root.querySelector<HTMLElement>('[data-axis=x] > .thumb')!;
    check(viewport.clientWidth === 260 && viewport.clientHeight === 120,
      'painted scrollbars preserve the full consumer viewport instead of consuming native scrollbar width');
    check(getComputedStyle(viewport).scrollbarWidth === 'none'
      && [...root.querySelectorAll<HTMLElement>('.rail')].every(rail => !rail.hidden),
    'both overflowing axes use visible painted rails with native bars hidden');
    area.scrollTo({ top: 500, left: 350 }); await frame(); await frame();
    check(area.scrollTop === 500 && area.scrollLeft === 350 && area.scrollHeight === 700 && area.scrollWidth === 900,
      'standard public scrolling methods and offsets address the actual viewport and complete content');
    check(Number(vertical.getAttribute('aria-valuenow')) > 0 && Number(horizontal.getAttribute('aria-valuenow')) > 0,
      'painted scrollbar positions expose the current native offsets');
    area.focus();
    check(root.activeElement === viewport, 'explicit scroll-area focus reaches the keyboard viewport');
    vertical.dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true }));
    await frame();
    check(area.scrollTop === 0 && area.scrollLeft === 350, 'vertical scrollbar Home keeps the horizontal position');
    horizontal.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
    await frame();
    check(area.scrollLeft === 640, 'horizontal scrollbar End reaches the last content column');
    area.direction = 'vertical'; await frame(); await frame();
    check(root.querySelector<HTMLElement>('.rail[data-axis=x]')!.hidden
      && !root.querySelector<HTMLElement>('.rail[data-axis=y]')!.hidden,
    'the public direction property controls painted and native scrolling together');
    area.scrollTo({ top: 450 }); await frame();
    area.hidden = true; await frame();
    area.hidden = false; await frame(); await frame();
    check(area.scrollTop === 450 && viewport.clientWidth === 260,
      'hiding and restoring the same scroll area retains its offset and full content width');
    area.style.height = ''; area.style.maxHeight = '120px'; area.style.padding = '10px'; area.style.boxSizing = 'border-box';
    await frame(); await frame();
    check(viewport.getBoundingClientRect().bottom <= area.getBoundingClientRect().bottom - 9
      && viewport.clientHeight <= 100,
    'a padded maximum-height consumer keeps the complete scroll viewport inside its actual content box');
    area.scrollTo({ top: 10000 }); await frame();
    check(area.scrollTop === area.scrollHeight - viewport.clientHeight,
      'the final record remains reachable when a padded modal or popup constrains maximum height');
  } finally { area.remove(); }
  check(!area.isConnected, 'scroll-area removal releases observers and pointer ownership');
  const text = document.createElement('nexus-text-area') as UiElement<'nexus-text-area'>;
  text.rows = 3; text.value = Array.from({ length: 80 }, (_, index) => 'Line ' + index).join('\n');
  fixture.append(text);
  try {
    await frame(); await frame();
    const field = text.shadowRoot!.querySelector('textarea')!;
    const rail = text.shadowRoot!.querySelector<HTMLElement>('[data-axis=y]')!;
    check(getComputedStyle(field).scrollbarWidth === 'none' && !rail.hidden,
      'multiline input shares painted rails while retaining the real editable textarea');
    rail.firstElementChild!.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
    await frame();
    check(field.scrollTop === field.scrollHeight - field.clientHeight && field.value.endsWith('Line 79'),
      'multiline input reaches its last line without replacing or truncating the editable value');
    text.readOnly = true; text.focus();
    check(text.shadowRoot!.activeElement === field && field.readOnly,
      'read-only multiline text retains native caret, selection and keyboard focus');
  } finally { text.remove(); }
  const tableArea = document.createElement('nexus-scroll-area') as UiElement<'nexus-scroll-area'>;
  tableArea.direction = 'horizontal'; tableArea.label = 'Running tasks';
  tableArea.style.cssText = 'width:500px;height:120px';
  const table = document.createElement('table');
  table.style.cssText = 'width:100%;table-layout:fixed;border-collapse:collapse';
  table.innerHTML = '<colgroup>' + '<col style="width:20%">'.repeat(5) + '</colgroup><thead><tr>'
    + ['Task','Type','Mode','Progress','Status'].map(label => '<th>' + label + '</th>').join('') + '</tr></thead>';
  tableArea.append(table); fixture.append(tableArea);
  try {
    await frame(); await frame();
    const viewport = tableArea.shadowRoot!.querySelector<HTMLElement>('.viewport')!.getBoundingClientRect();
    check(Math.abs(table.getBoundingClientRect().width - viewport.width) <= 1
      && [...table.querySelectorAll('th')].every(cell => cell.getBoundingClientRect().right <= viewport.right + 1),
    'percentage-width table columns remain within the complete horizontal scroll viewport');
  } finally { tableArea.remove(); }
}
