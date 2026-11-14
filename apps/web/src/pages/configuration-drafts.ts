import type { ConfigurationStore } from '../app/store-ports';

export function configurationDrafts(store: ConfigurationStore, bindingId: string) {
  return store.editors.target(store.state.capabilities?.instanceId || '', 'bindings', bindingId).child('configuration');
}
