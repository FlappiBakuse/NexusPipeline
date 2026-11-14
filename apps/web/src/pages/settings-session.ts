import { computed, watch } from 'vue';
import type { AppStore } from '../app/store';
import { SettingsWrites } from '../app/settings-writes';
import { completedSettings, savedSettings } from '../control/settings-command';
import { objectValue } from '../control/client';
import { settings } from '../control/models';
import type { ControlHostSettingsValues, ControlHostSettings } from '../control/wire.generated';

const owners = new WeakMap<AppStore, Map<string, SettingsWrites<ControlHostSettingsValues>>>();

export function useSettingsSession(store: AppStore, valid: () => boolean) {
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
      return store.state.settings!.saved;
    };
    const body = (value: { id: string; revision: number; values: ControlHostSettingsValues }): ControlHostSettings => ({
      format: 'nexus.control.host-settings', version: 1, operationId: value.id,
      expectedRevision: value.revision, values: value.values
    });
    const read = async () => {
      const snapshot = await store.client.read('/control/v1/settings', settings);
      current();
      if (snapshot.saved.revision >= store.state.settings!.saved.revision) store.state.settings = snapshot;
      return snapshot.saved;
    };
    owner = new SettingsWrites({ current, signal: store.editors.signal, read,
      write: async value => {
        const command = body(value);
        await store.client.write('/control/v1/settings', command, result => savedSettings(result, command), value.id);
        current();
        const saved = { revision: value.revision + 1, values: value.values };
        store.state.settings = { ...store.state.settings!, saved };
        try { return await read(); }
        catch (reason) { store.report(reason); return saved; }
      },
      inspect: async value => {
        const result = await store.client.read('/control/v1/operations/' + value.id, objectValue);
        return completedSettings(result, body(value)) ? read() : undefined;
      } });
    instances.set(instance, owner);
  }
  const writer = owner;
  writer.observe(store.state.settings!.saved);
  watch(() => store.state.settings?.saved, value => { if (value) writer.observe(value); });
  return { session: writer.state, locked: computed(() => writer.state.unknown),
    patch: (values: Partial<ControlHostSettingsValues>) => {
      if (valid() && store.can('settings.write')) void writer.patch(values).catch(reason => {
        if (store.state.capabilities?.instanceId === instance) store.report(reason);
      });
    } };
}
