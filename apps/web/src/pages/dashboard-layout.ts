import { computed, reactive, shallowRef, toRefs, watch } from 'vue';
import type { AppStore } from '../app/store';
import { interfaceSettings } from '../app/interface-settings';
import { dashboardCards, dashboardDefaults, type DashboardCard } from '../control/interface';

type Layout = { dashboardCards: DashboardCard[]; dashboardAvailableCards: DashboardCard[] };

export function useDashboardLayout(store: AppStore, contributions: () => DashboardCard[] = () => [],
  catalog: () => DashboardCard[] = () => [...contributions(), ...dashboardDefaults]) {
  const defaults = () => [...contributions(), ...dashboardDefaults];
  const owner = shallowRef<ReturnType<typeof interfaceSettings>>();
  const preview = shallowRef<Layout>();
  const state = reactive({ editing: false, saving: false, error: '', notice: '' });
  let instance = '';
  function commands() {
    if (!store.state.interface) throw Error('布局读取未完成。');
    const current = store.state.capabilities!.instanceId;
    if (!owner.value || instance !== current) {
      owner.value = interfaceSettings(store); instance = current;
    }
    return owner.value;
  }
  const selected = () => {
    if (preview.value) return preview.value.dashboardCards;
    const snapshot = store.state.interface;
    if (!snapshot) return defaults();
    const values = commands().state.values;
    const cards = dashboardCards(values.dashboardCards);
    const hasDefaultLayout = values.dashboardAvailableCards === undefined &&
      cards.length === dashboardDefaults.length && cards.every((id, index) => id === dashboardDefaults[index]);
    return hasDefaultLayout ? defaults() : cards;
  };
  const available = () => preview.value?.dashboardAvailableCards ?? dashboardCards([
    ...(commands().state.values.dashboardAvailableCards ?? []), ...catalog()
  ]).filter(id => !selected().includes(id));
  watch(() => store.state.interface, value => { if (value) commands().observe(value); }, { immediate: true });
  const close = () => { state.editing = false; preview.value = undefined; };
  function change(cards: DashboardCard[], availableCards = available()): void {
    if (!state.editing || state.saving || !store.can('settings.write')) return;
    preview.value = { dashboardCards: dashboardCards(cards),
      dashboardAvailableCards: dashboardCards([...availableCards, ...selected()]).filter(id => !cards.includes(id)) };
  }
  async function begin(): Promise<void> {
    if (!store.state.interface) {
      try { await store.inspectInterface(); } catch (reason) { store.report(reason); return; }
    }
    const writer = commands(); writer.observe(store.state.interface!);
    if (writer.state.pending) { await writer.resume(); if (writer.state.pending) return; }
    preview.value = { dashboardCards: [...selected()], dashboardAvailableCards: [...available()] };
    state.editing = true; state.error = ''; state.notice = '';
  }
  async function save(): Promise<void> {
    const value = preview.value;
    if (!value || state.saving || !store.can('settings.write')) return;
    state.saving = true;
    try {
      const writer = commands();
      await writer.patch(value);
      if (!writer.state.error && preview.value === value) close();
    } catch (reason) { store.report(reason); }
    finally { state.saving = false; }
  }
  const { editing, error, notice } = toRefs(state);
  return {
    layout: computed(() => store.state.interface),
    layoutCards: computed(() => state.editing ? selected() : null),
    layoutAvailableCards: computed(() => state.editing ? available() : []),
    layoutOpen: editing, layoutBusy: computed(() => state.saving || owner.value?.state.unknown || false),
    layoutError: computed(() => error.value || owner.value?.state.error || ''), layoutNotice: notice,
    cards: computed(selected), beginLayout: begin, cancelLayout: close, saveLayout: save,
    readLayout: async () => { try { const latest = await store.inspectInterface(); commands().observe(latest); } catch (reason) { store.report(reason); } },
    changeLayout: (cards: DashboardCard[]) => change(cards),
    changeAvailableLayout: (cards: DashboardCard[]) => {
      const ids = available();
      if (cards.length === ids.length && new Set(cards).size === cards.length && cards.every(id => ids.includes(id)))
        change(selected(), cards);
    },
    resetLayout: () => change(defaults(), catalog())
  };
}
