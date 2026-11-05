import { modalExitDuration } from './modal-surface';

export function leaveModal(element: Element, done: () => void): void {
  if (!(element instanceof HTMLElement)) {
    done();
    return;
  }
  element.inert = true;
  // Vue retains leaving DOM for the animation; credentials must leave it immediately.
  element
    .querySelectorAll<HTMLInputElement>('input[type=password],input[autocomplete=new-password]')
    .forEach((input) => {
      input.value = '';
    });
  element
    .querySelectorAll<HTMLElement & { type?: string; value: string }>('nexus-text-input')
    .forEach((input) => {
      if (input.type === 'password') input.value = '';
    });
  (element as HTMLElement & { open: boolean }).open = false;
  if (matchMedia('(prefers-reduced-motion: reduce)').matches) done();
  else setTimeout(done, modalExitDuration + 16);
}
