import { t } from './locale';
import { leaveModal } from '../ui/modal-surface';
import type { ConfirmDialog } from '../ui/dialog-elements';

let pending: Promise<boolean> | undefined;
export function confirmAction(message: string, signal?: AbortSignal,
  options: { title?: string; confirmLabel?: string } = {}): Promise<boolean> {
  if (pending || signal?.aborted) return Promise.resolve(false);
  pending = new Promise<boolean>((resolve) => {
    const dialog = document.createElement('nexus-confirm-dialog') as ConfirmDialog;
    Object.assign(dialog, { open: true, title: options.title || t('确认'), message, confirmLabel: options.confirmLabel || '' });
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
