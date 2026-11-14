import type { ControlClient } from '../control/client';
import type { ExitReadiness } from './desktop';

export function prepareAppExit(
  client: Pick<ControlClient, 'pendingWrites' | 'canExitMaintenance'>
) {
  return async (reason: 'close' | 'maintenance'): Promise<ExitReadiness> => {
    const ready = () => reason === 'maintenance' ? client.canExitMaintenance() : client.pendingWrites === 0;
    return ready() ? 'ready' : 'pending-writes';
  };
}
