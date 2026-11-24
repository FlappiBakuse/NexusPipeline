import type { AppStore } from './store';

export type SurfaceStore = Pick<AppStore, 'state' | 'report'>;
export type NavigationStore = Pick<AppStore, 'state' | 'client' | 'can' | 'report' | 'navigate'>;
export type ConfigurationStore = Pick<AppStore, 'state' | 'client' | 'can' | 'report' | 'drafts' | 'dirty' | 'editors' | 'readEditorDirectory'>;
export type RecordsStore = Pick<AppStore, 'state' | 'client' | 'can' | 'report' | 'navigate' | 'logs' | 'media'>;
export type AutomationStore = ConfigurationStore & Pick<AppStore, 'navigate' | 'change' | 'command' | 'refresh' | 'refreshCatalog'>;
