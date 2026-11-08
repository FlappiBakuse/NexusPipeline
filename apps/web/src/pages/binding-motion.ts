import { nextTick } from 'vue';

export async function animateBindingLayout(root: HTMLElement | undefined, change: () => void): Promise<void> {
  const cards = () => [...(root?.querySelectorAll<HTMLElement>('.binding') || [])];
  const previous = new Map(cards().map(card => [card, card.getBoundingClientRect()]));
  for (const card of cards()) for (const animation of card.getAnimations()) animation.cancel();
  change();
  await nextTick();
  if (matchMedia('(prefers-reduced-motion: reduce)').matches || !root?.isConnected) return;
  const configured = getComputedStyle(root).getPropertyValue('--nx-motion-duration-normal');
  if (configured && parseFloat(configured) < 1) return;
  for (const card of cards()) {
    const before = previous.get(card), after = card.getBoundingClientRect();
    if (!before || !after.width || !after.height) continue;
    card.animate([
      { transform: `translate(${before.left - after.left}px,${before.top - after.top}px) scale(${before.width / after.width},${before.height / after.height})`, transformOrigin: 'top left' },
      { transform: 'none', transformOrigin: 'top left' }
    ], { duration: 180, easing: 'cubic-bezier(.2,.8,.2,1)' });
  }
}
