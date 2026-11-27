import { computed, watch } from 'vue';
import type { AppStore } from '../app/store';
import { SettingsWrites } from '../app/settings-writes';
import { completedSettings, savedSettings } from '../control/settings';
import { objectValue } from '../control/client';
import type { ControlHostSettingsValues, ControlHostSettings } from '../control/wire.generated';

const owners = new WeakMap<AppStore, Map<string, SettingsWrites<ControlHostSettingsValues>>>();

export function useSettingsSession(store: AppStore, valid: (category: string) => boolean) {
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
      if (store.state.capabilities?.instanceId !== instance || !store.state.settings) throw new Error('control_instance_changed');
      return store.state.settings.saved;
    };
    const body = (value: { id: string; revision: number; values: ControlHostSettingsValues }): ControlHostSettings => ({
      format: 'nexus.control.host-settings', version: 1, operationId: value.id,
      expectedRevision: value.revision, values: value.values
    });
    const read = async (accepted?: number, authority = store.client.authoritySignal) => {
      await store.refreshSettings(accepted, authority);
      return current();
    };
    owner = new SettingsWrites({ current, signal: store.editors.signal, read,
      write: async value => {
        const command = body(value);
        const authority = store.client.authoritySignal;
        const startupChanged = current().values.autoStart !== value.values.autoStart;
        const receipt = await store.client.write('/control/v1/settings', command, result => savedSettings(result, command), value.id, authority);
        authority.throwIfAborted();
        const saved = { revision: value.revision + 1, values: value.values };
        if (current().revision < saved.revision) store.state.settings = { ...store.state.settings!, saved };
        // Startup registration can finish after a same-revision SSE read has started.
        try { return await read(startupChanged || receipt.startupEffect != null ? undefined : saved.revision, authority); }
        catch { authority.throwIfAborted(); return current(); }
      },
      inspect: async value => {
        const authority = store.client.authoritySignal;
        const result = await store.client.read('/control/v1/operations/' + value.id, objectValue, authority);
        return completedSettings(result, body(value)) ? read(undefined, authority) : undefined;
      } });
    instances.set(instance, owner);
  }
  const writer = owner;
  writer.observe(store.state.settings!.saved);
  watch(() => store.state.settings?.saved, value => { if (value) writer.observe(value); });
  return { session: writer.state, locked: computed(() => writer.state.unknown),
    patch: (category: string, values: Partial<ControlHostSettingsValues>) => {
      if (store.can('settings.write') && valid(category)) void writer.patch(values).catch(reason => {
        if (!store.editors.signal.aborted && store.state.capabilities?.instanceId === instance && !writer.state.error)
          writer.state.error = reason instanceof Error ? reason.message : '设置保存失败。';
      });
    } };
}
