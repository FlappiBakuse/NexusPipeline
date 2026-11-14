import { t } from './locale';
import { leaveModal } from '../ui/modal-transition';

let pending: Promise<boolean> | undefined;
export function confirmAction(message: string, signal?: AbortSignal): Promise<boolean> {
  if (pending || signal?.aborted) return Promise.resolve(false);
  pending = new Promise<boolean>((resolve) => {
    const dialog = document.createElement('nexus-confirm-dialog');
    Object.assign(dialog, { open: true, title: t('确认'), message });
    const scope = new AbortController();
    const finish = (value: boolean): void => {
      if (scope.signal.aborted) return;
      scope.abort();
      leaveModal(dialog, () => {
        dialog.remove();
        resolve(value);
      });
    };
    dialog.addEventListener('nexus-confirm', () => finish(true), { signal: scope.signal });
    dialog.addEventListener('nexus-cancel', () => finish(false), { signal: scope.signal });
    dialog.addEventListener('nexus-close', () => finish(false), { signal: scope.signal });
    window.addEventListener('hashchange', () => finish(false), { signal: scope.signal });
    signal?.addEventListener('abort', () => finish(false), { once: true, signal: scope.signal });
    document.body.append(dialog);
  }).finally(() => {
    pending = undefined;
  });
  return pending;
}
