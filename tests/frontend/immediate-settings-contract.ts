import { createApp, h, nextTick, ref } from 'vue';
import SettingsFields from '../../apps/web/src/pages/SettingsFields.vue';
import FormSelect from '../../apps/web/src/components/FormSelect';
import type { ControlHostSettingsValues } from '../../apps/web/src/control/wire.generated';
import { SettingsWrites } from '../../apps/web/src/app/settings-writes';
import { ControlFailure } from '../../apps/web/src/control/client';

type Check = (value: unknown, name: string) => void;
type Values = { enabled: boolean; port: number; style: { dim: number; blur: number } };
const initial = (): Values => ({ enabled: false, port: 4200, style: { dim: 10, blur: 0 } });

export async function immediateSettingsContract(check: Check): Promise<void> {
  const lifetime = new AbortController();
  let snapshot = { revision: 1, values: initial() };
  let release!: () => void, held = new Promise<void>(resolve => { release = resolve; });
  const writes: string[] = [];
  const writer = new SettingsWrites({ signal: lifetime.signal, current: () => snapshot,
    read: async () => snapshot, inspect: async () => undefined,
    write: async value => {
      writes.push(value.id); if (writes.length === 1) await held;
      if (value.revision !== snapshot.revision) throw new ControlFailure(409, 'revision_conflict');
      snapshot = { revision: value.revision + 1, values: value.values }; return snapshot;
    } });
  const first = writer.patch({ enabled: true });
  await Promise.resolve(); await Promise.resolve();
  const second = writer.patch({ port: 4300 });
  const third = writer.patch({ style: { dim: 30, blur: 2 } });
  release(); await Promise.all([first, second, third]);
  check(snapshot.values.enabled && snapshot.values.port === 4300 && snapshot.values.style.dim === 30,
    'immediate settings serialize rapid changes without losing fields');
  const count = writes.length; await writer.patch({ port: 4300 });
  check(writes.length === count, 'selecting an unchanged setting produces no business write');
  snapshot = { revision: snapshot.revision + 1, values: { ...snapshot.values, port: 4400 } };
  await writer.patch({ enabled: false });
  check(!snapshot.values.enabled && snapshot.values.port === 4400,
    'an unrelated external settings change survives a local update');
  lifetime.abort();
  await failedAndUnknown(check);
  await proxyCompletion(check);
}

async function failedAndUnknown(check: Check): Promise<void> {
  const lifetime = new AbortController();
  let snapshot = { revision: 1, values: initial() }, sends = 0, original = '';
  let mode: 'fail' | 'unknown' = 'fail';
  const writer = new SettingsWrites({ signal: lifetime.signal, current: () => snapshot,
    read: async () => snapshot,
    inspect: async value => {
      check(value.id === original, 'settings confirmation reads the original operation');
      return snapshot;
    },
    write: async value => {
      sends++; original = value.id;
      if (mode === 'fail') throw new Error('controlled settings failure');
      snapshot = { revision: value.revision + 1, values: value.values };
      throw new ControlFailure(0, 'reply_lost', undefined, value.id, true);
    } });
  await writer.patch({ enabled: true });
  check(!writer.state.values.enabled && !!writer.state.error,
    'a definite settings failure restores the confirmed visible value');
  mode = 'unknown'; await writer.patch({ enabled: true });
  check(writer.state.values.enabled && !writer.state.pending && !writer.state.error && sends === 2,
    'a lost settings reply is confirmed by reading without a second save');
  lifetime.abort();
}
async function proxyCompletion(check: Check): Promise<void> {
  const holder = document.createElement('section'); document.body.append(holder);
  const value = ref({ proxy: { mode: 'none', url: null, userName: '' } } as ControlHostSettingsValues);
  const visible = ref(true);
  const patches: Partial<ControlHostSettingsValues>[] = [];
  const app = createApp({ render: () => visible.value ? h(SettingsFields, { category: 'proxy',
    value: value.value, disabled: false, locale: 'zh-CN', localeDisabled: false,
    onPatch: patch => { patches.push(patch); value.value = { ...value.value, ...patch }; } }) : null });
  app.component('FormSelect', FormSelect); app.mount(holder);
  const mode = () => holder.querySelector('nexus-select')!;
  const choose = (index: string) => mode().dispatchEvent(new CustomEvent('nexus-change',
    { detail: { value: index }, bubbles: true, composed: true }));
  try {
    choose('2'); await nextTick();
    check(patches.length === 0 && !!holder.querySelector('input[type=url]'),
      'choosing HTTP reveals its required address without submitting incomplete settings');
    const address = holder.querySelector<HTMLInputElement>('input[type=url]')!;
    address.value = 'http://127.0.0.1:7890'; address.dispatchEvent(new Event('change', { bubbles: true }));
    await nextTick();
    check(patches.length === 1 && value.value.proxy.mode === 'http' && value.value.proxy.url === address.value,
      'a completed proxy address immediately saves the mode and address together');
    choose('0'); await nextTick();
    value.value = { ...value.value, proxy: { mode: 'none', url: null, userName: '' } }; await nextTick();
    choose('2'); await nextTick(); const completed = patches.length;
    visible.value = false; await nextTick(); visible.value = true; await nextTick();
    check(patches.length === completed && !holder.querySelector('input[type=url]'),
      'leaving unfinished proxy input discards it without retaining a settings draft');
  } finally { app.unmount(); holder.remove(); }
}
