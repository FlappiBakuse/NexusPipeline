import type { ControlClient } from '../control/client';
import type { ExitReadiness } from './desktop';

export function prepareAppExit(client: Pick<ControlClient, 'pendingWrites'>) {
  return async (): Promise<ExitReadiness> => client.pendingWrites === 0 ? 'ready' : 'pending-writes';
}
