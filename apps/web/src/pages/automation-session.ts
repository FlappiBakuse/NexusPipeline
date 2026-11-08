import type { AutomationStore } from '../app/store-ports';
import type { ControlAutomationChange, ControlAutomationImport, ControlProjectCommand } from '../control/wire.generated';

export type AutomationAction = {
  id: string;
  action: 'save' | 'remove' | 'move' | 'import' | 'avatar';
  key?: string;
  name?: string;
  target: string;
  kind: 'scripts' | 'users' | 'queues';
  body: ControlAutomationChange | ControlAutomationImport;
  sourceSelection?: ControlProjectCommand;
  definitionCommitted?: boolean;
};
export type AutomationSession = {
  revisions: Record<string, number>;
  catalogs: Record<string, number>;
  pending: AutomationAction | null;
  saving: boolean;
  error: string;
  notice: string;
};

export function automationSession(store: AutomationStore, page: string): AutomationSession {
  const key = 'automation/page/' + store.state.capabilities?.instanceId + '/' + page;
  if (!store.drafts.has(key))
    store.drafts.set(key, {
      revisions: {},
      catalogs: {},
      pending: null,
      saving: false,
      error: '',
      notice: ''
    });
  return store.drafts.get(key) as unknown as AutomationSession;
}
