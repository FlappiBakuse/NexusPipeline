import type { UiElement } from '@nexuspipeline/extension-sdk';

export async function feedbackLayoutContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const field = document.createElement('nexus-text-input') as UiElement<'nexus-text-input'>;
  const clipped = document.createElement('div');
  clipped.style.cssText = 'overflow:hidden;width:240px'; clipped.append(field); fixture.append(clipped);
  const input = field.shadowRoot!.querySelector('input')!; input.focus();
  const bounds = clipped.getBoundingClientRect(), fieldBounds = input.getBoundingClientRect();
  check(document.activeElement === field && field.shadowRoot!.activeElement === input &&
    fieldBounds.left >= bounds.left && fieldBounds.right <= bounds.right,
    'a field inside a clipped container remains visible and keyboard focusable');
  clipped.remove();
  const pause = () => new Promise(resolve => setTimeout(resolve, 260));
  const card = document.createElement('nexus-collapsible-card') as UiElement<'nexus-collapsible-card'>;
  card.title = 'Display settings'; card.description = 'Wallpaper and effects';
  const content = document.createElement('input'); content.value = 'retained draft';
  card.append(content); fixture.append(card); await pause();
  const closed = card.getBoundingClientRect().height;
  card.expanded = true;
  await new Promise(resolve => requestAnimationFrame(resolve));
  await pause(); const opened = card.getBoundingClientRect().height;
  check(opened > closed && card.shadowRoot!.textContent!.includes('Wallpaper and effects'),
    'the shared card displays its description and open content');
  card.expanded = false;
  check((card.shadowRoot!.querySelector('.reveal') as HTMLElement).inert,
    'closing immediately removes content from interaction');
  await new Promise(resolve => requestAnimationFrame(resolve));
  await pause();
  check(Math.abs(card.getBoundingClientRect().height - closed) < 1 && content.value === 'retained draft',
    'collapse restores the header height and preserves the draft');
  const first = document.createElement('nexus-toast') as UiElement<'nexus-toast'>;
  const second = document.createElement('nexus-toast') as UiElement<'nexus-toast'>;
  first.message = 'An operation needs attention'; second.message = 'Appearance changed';
  fixture.append(first, second); await pause();
  const a = first.getBoundingClientRect(), b = second.getBoundingClientRect();
  check(b.right <= innerWidth && b.bottom <= innerHeight && b.left > innerWidth / 2 &&
    b.top > innerHeight / 2 && a.top >= 0 && a.left >= 0 && a.right <= innerWidth && a.bottom < b.top,
    'all toast types stack at the viewport bottom right without overlap');
  second.remove(); await pause();
  check(Math.abs(first.getBoundingClientRect().bottom - b.bottom) < 1,
    'dismissing a toast closes the gap in the stack');
  first.remove(); card.remove();
  const modal = document.createElement('nexus-modal') as UiElement<'nexus-modal'>;
  modal.title = 'Editing configuration'; modal.open = true;
  fixture.append(modal);
  const toast = document.createElement('nexus-toast') as UiElement<'nexus-toast'>;
  toast.message = 'Configuration saved'; fixture.append(toast);
  await pause();
  check(toast.matches(':popover-open') && !toast.shadowRoot!.querySelector('button'),
    'notifications enter the browser top layer without internal actions');
  const nested = document.createElement('nexus-modal') as UiElement<'nexus-modal'>;
  nested.title = 'Another editor'; nested.open = true; fixture.append(nested); await pause();
  check(toast.matches(':popover-open'), 'opening another modal preserves the visible top-layer notification');
  await new Promise(resolve => setTimeout(resolve, 4200));
  check(!toast.matches(':popover-open') && modal.open && nested.open,
    'ordinary notifications dismiss automatically without closing or changing an editor');
  toast.remove(); nested.remove(); modal.remove();
}
