import type { AppStore } from './store';
import { SettingsWrites } from './settings-writes';
import type { InterfaceValues } from '../control/interface';

const owners = new WeakMap<AppStore, Map<string, SettingsWrites<InterfaceValues>>>();

export function interfaceSettings(store: AppStore): SettingsWrites<InterfaceValues> {
  const instance = store.state.capabilities!.instanceId;
  let instances = owners.get(store);
  if (!instances) {
    instances = new Map(); owners.set(store, instances);
    store.editors.signal.addEventListener('abort', () => instances!.clear(), { once: true });
  }
  for (const key of instances.keys()) if (key !== instance) instances.delete(key);
  let owner = instances.get(instance);
  if (!owner) {
    const current = () => {
      if (store.state.capabilities?.instanceId !== instance) throw new Error('control_instance_changed');
      return store.state.interface!;
    };
    owner = new SettingsWrites({ current, signal: store.editors.signal,
      write: value => store.saveInterface(value.values, value.revision, value.id),
      read: () => store.inspectInterface(),
      inspect: async () => {
        const latest = await store.inspectInterface();
        return store.interfaceChange.pending ? undefined : latest;
      } });
    instances.set(instance, owner);
  }
  owner.observe(store.state.interface!);
  return owner;
}
