import { shallowReactive } from 'vue';
import type { AutomationSnapshot, Capabilities, SettingsSnapshot, HostStatus } from '../control/models';
import type { InterfaceSnapshot } from '../control/interface';
import type { AppRoute } from './navigation';

export function createHostState() {
  return shallowReactive({
    page: 'dashboard' as AppRoute,
    visible: !document.hidden,
    busy: false,
    error: '',
    notice: '',
    capabilities: null as Capabilities | null,
    automation: null as AutomationSnapshot | null,
    settings: null as SettingsSnapshot | null,
    status: null as HostStatus | null,
    revision: 0,
    uncertainOperation: '',
    feedError: '',
    interface: null as InterfaceSnapshot | null,
    events: null as Record<string, unknown> | null
  });
}

export type HostState = ReturnType<typeof createHostState>;
