import type { AppStore } from './store';

export type SurfaceStore = Pick<AppStore, 'state' | 'report'>;
export type NavigationStore = Pick<AppStore, 'state' | 'client' | 'can' | 'report' | 'navigate'>;
export type ConfigurationStore = Pick<AppStore, 'state' | 'client' | 'can' | 'report' | 'drafts' | 'dirty'>;
export type RecordsStore = Pick<AppStore, 'state' | 'client' | 'can' | 'report' | 'navigate' | 'logs' | 'media'>;
export type AutomationStore = Pick<AppStore, 'state' | 'client' | 'can' | 'report' | 'navigate' | 'drafts' | 'dirty' | 'change' | 'command' | 'refresh'>;
