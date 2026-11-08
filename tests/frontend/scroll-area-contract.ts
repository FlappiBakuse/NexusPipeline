import type { UiElement } from '@nexuspipeline/extension-sdk';

const frame = () => new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
export async function scrollAreaContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const area = document.createElement('nexus-scroll-area') as UiElement<'nexus-scroll-area'>;
  area.direction = 'both'; area.label = 'Large record table';
  area.style.cssText = 'width:260px;height:120px';
  const content = document.createElement('div');
  content.style.cssText = 'width:900px;height:700px;display:flex;align-items:end;justify-content:end';
  const last = document.createElement('span'); last.textContent = 'Last record';
  content.append(last); area.append(content); fixture.append(area);
  try {
    await frame(); await frame();
    area.focus();
    const rail = area.shadowRoot!.querySelector<HTMLElement>('.rail[data-axis=y]')!;
    check(getComputedStyle(rail).opacity === '0', 'focus alone leaves an idle scrollbar hidden');
    area.scrollTo({ top: 10000, left: 10000 }); await frame(); await frame();
    await new Promise(resolve => setTimeout(resolve, 200));
    check(getComputedStyle(rail).opacity === '1', 'scrolling reveals the custom scrollbar');
    await new Promise(resolve => setTimeout(resolve, 2100));
    check(getComputedStyle(rail).opacity === '0', 'the scrollbar hides after two seconds even with retained focus');
    const visible = area.getBoundingClientRect(), end = last.getBoundingClientRect();
    check(area.scrollTop > 0 && area.scrollLeft > 0 && end.bottom <= visible.bottom && end.right <= visible.right,
      'the final row and column remain reachable through the public scroll API');
    area.focus();
    check(document.activeElement === area, 'the scroll area accepts explicit keyboard focus');
    area.hidden = true; await frame(); area.hidden = false; await frame();
    check(area.scrollTop > 0 && last.textContent === 'Last record', 'hiding and returning preserves the current content position');
  } finally { area.remove(); }
  const text = document.createElement('nexus-text-area') as UiElement<'nexus-text-area'>;
  text.rows = 3; text.value = Array.from({ length: 80 }, (_, index) => 'Line ' + index).join('\n');
  fixture.append(text);
  try {
    text.readOnly = true; text.focus(); await frame();
    check(document.activeElement === text && text.value.endsWith('Line 79'),
      'read-only long text remains complete and focusable');
  } finally { text.remove(); }
}
