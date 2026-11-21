import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle, ControlAutomationChange } from '../control/wire.generated';
import { copyDraft } from '../app/draft-copy';
import { prepareHooks, hookPathKey } from './hook-paths';
import { objectValue } from '../control/client';

type Binding = AutomationBundle['bindings'][number];
export type AccountDraft = {
  items: Binding[];
  baseline: Binding[];
  catalogRevision: number;
  revisions: Record<string, number>;
  candidates: Record<string, string>;
};
export const accountKey = (store: AutomationStore, id: string): string =>
  'account-bindings/' + store.state.capabilities?.instanceId + '/' + id;

export function accountDraft(store: AutomationStore, id: string): AccountDraft {
  const key = accountKey(store, id);
  const retained = store.drafts.get(key);
  if (retained) return retained as AccountDraft;
  const catalog = store.state.automation!;
  const items = copyDraft(catalog.bundle.bindings.filter((item) => item.userId === id)).sort(
    (a, b) => a.priority - b.priority
  );
  store.drafts.set(key, {
    items,
    baseline: copyDraft(items),
    catalogRevision: catalog.catalogRevision,
    candidates: {},
    revisions: Object.fromEntries(
      items.map((item) => [
        item.id,
        catalog.revisions.find((value) => value.kind === 'binding' && value.id === item.id)?.revision || 0
      ])
    )
  });
  return store.drafts.get(key) as AccountDraft;
}

export function accountChanges(
  store: AutomationStore,
  id: string,
  command: ControlAutomationChange
): ControlAutomationChange {
  const state = store.drafts.get(accountKey(store, id)) as AccountDraft | undefined;
  if (!state) return command;
  const roots = [...command.roots];
  const items = state.items.map((item) => prepareHooks(store, item.id, copyDraft(item), roots));
  const changed = items.filter(
    (item) => JSON.stringify(item) !== JSON.stringify(state.baseline.find((value) => value.id === item.id))
  );
  return {
    ...command,
    roots,
    expectedCatalogRevision: state.catalogRevision,
    bindings: changed.map((value) => ({ expectedRevision: state.revisions[value.id] || 0, value })),
    removed: [
      ...command.removed,
      ...state.baseline
        .filter((item) => !items.some((value) => value.id === item.id))
        .map((item) => ({
          kind: 'binding' as const,
          id: item.id,
          expectedRevision: state.revisions[item.id]!
        }))
    ]
  };
}

export function clearAccountDraft(store: AutomationStore, id: string, reason: 'cancel' | 'saved' = 'cancel'): void {
  const state = store.drafts.get(accountKey(store, id)) as AccountDraft | undefined;
  for (const binding of [...(state?.items || []), ...(state?.baseline || [])]) {
    store.drafts.delete(hookPathKey(store, binding.id));
    if (reason === 'saved' && !state?.candidates[binding.id])
      store.editors.saved(store.state.capabilities?.instanceId || '', 'bindings', binding.id);
    else store.editors.settle(store.state.capabilities?.instanceId || '', 'bindings', binding.id, reason);
  }
  if (reason === 'cancel') for (const candidate of Object.values(state?.candidates || {})) discardCandidate(store, candidate);
  store.drafts.delete(accountKey(store, id));
}

function discardCandidate(store: AutomationStore, id: string): void {
  void store.client.write('/control/v1/configuration/editor/drafts/' + encodeURIComponent(id) + '/discard', {},
    objectValue, id).catch(() => undefined);
}

export function retainBindingCandidate(store: AutomationStore, userId: string, bindingId: string, id: string): void {
  const state = accountDraft(store, userId);
  const previous = state.candidates[bindingId];
  if (previous && previous !== id) discardCandidate(store, previous);
  state.candidates[bindingId] = id;
}

export function discardBindingCandidate(store: AutomationStore, userId: string, bindingId: string): void {
  const candidates = accountDraft(store, userId).candidates;
  const candidate = candidates[bindingId];
  if (!candidate) return;
  discardCandidate(store, candidate);
  delete candidates[bindingId];
}

export function replaceAccountBindings(store: AutomationStore, userId: string, items: Binding[]): boolean {
  const state = accountDraft(store, userId);
  const removed = state.items.filter(binding => !items.some(item => item.id === binding.id));
  const instance = store.state.capabilities?.instanceId || '';
  if (removed.some(binding => !store.editors.target(instance, 'bindings', binding.id).canCancel())) return false;
  for (const binding of removed) {
    store.editors.settle(instance, 'bindings', binding.id, 'cancel');
    store.drafts.delete(hookPathKey(store, binding.id));
    discardBindingCandidate(store, userId, binding.id);
  }
  state.items = items;
  return true;
}

export function canCancelAccount(store: AutomationStore, id: string): boolean {
  const state = store.drafts.get(accountKey(store, id)) as AccountDraft | undefined;
  return [...(state?.items || []), ...(state?.baseline || [])].every(binding =>
    store.editors.target(store.state.capabilities?.instanceId || '', 'bindings', binding.id).canCancel());
}

export function validAccountLimits(
  store: AutomationStore,
  user: AutomationBundle['users'][number],
  form?: HTMLElement
): boolean {
  const state = store.drafts.get(accountKey(store, user.id)) as AccountDraft | undefined;
  if (
    user.bindingOverrides?.general.maximumSuccessfulRunsPerDay !== 0 &&
    !state?.items.some((item) => item.maximumSuccessfulRunsPerDay === 0)
  )
    return true;
  form?.querySelectorAll<HTMLElement>('nexus-number-input').forEach((element) => {
    const input = element as HTMLElement & { value: number; max: number };
    if (input.value === 0 && input.max === 9999) element.focus();
  });
  return false;
}
