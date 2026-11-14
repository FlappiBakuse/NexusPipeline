import { computed, reactive, shallowRef, toRefs, watch } from 'vue';
import type { AppStore } from '../app/store';
import { interfaceSettings } from '../app/interface-settings';
import { dashboardCards, dashboardDefaults, type DashboardCard } from '../control/interface';

export function useDashboardLayout(store: AppStore, contributions: () => DashboardCard[] = () => []) {
  const defaults = () => [...contributions(), ...dashboardDefaults];
  const owner = shallowRef<ReturnType<typeof interfaceSettings>>();
  const state = reactive({ editing: false, error: '', notice: '' });
  let baseline: DashboardCard[] | undefined, owned: DashboardCard[] | undefined, instance = '';
  let changing = Promise.resolve();
  function commands() {
    if (!store.state.interface) throw Error('布局读取未完成。');
    const current = store.state.capabilities!.instanceId;
    if (!owner.value || instance !== current) {
      owner.value = interfaceSettings(store); instance = current;
    }
    return owner.value;
  }
  const selected = () => {
    const snapshot = store.state.interface;
    if (!snapshot) return defaults();
    const values = commands().state.values.dashboardCards;
    return snapshot.revision === 0 && JSON.stringify(values) === JSON.stringify(snapshot.values.dashboardCards)
      ? defaults() : dashboardCards(values);
  };
  watch(() => store.state.interface, value => { if (value) commands().observe(value); }, { immediate: true });
  const close = () => { state.editing = false; baseline = undefined; owned = undefined; };
  function change(cards: DashboardCard[]): void {
    if (!state.editing || !store.can('settings.write') || JSON.stringify(cards) === JSON.stringify(selected())) return;
    changing = commands().patch({ dashboardCards: dashboardCards(cards) }).then(saved => {
      owned = [...saved.values.dashboardCards];
    }).catch(reason => { store.report(reason); });
  }
  async function begin(): Promise<void> {
    if (!store.state.interface) {
      try { await store.inspectInterface(); } catch (reason) { store.report(reason); return; }
    }
    await changing;
    const writer = commands(); writer.observe(store.state.interface!);
    if (writer.state.pending) { await writer.resume(); if (writer.state.pending) return; }
    baseline = [...selected()]; owned = [...baseline];
    state.editing = true; state.error = ''; state.notice = '';
  }
  async function cancel(): Promise<void> {
    try {
      const writer = commands();
      await changing;
      await writer.settled();
      const current = dashboardCards(store.state.interface!.values.dashboardCards);
      if (JSON.stringify(current) !== JSON.stringify(owned) &&
          JSON.stringify(current) !== JSON.stringify(baseline)) throw Error('布局已在其他位置更改，请检查当前值。');
      if (baseline) await writer.patch({ dashboardCards: [...baseline] });
      if (!writer.state.error) close();
    } catch (reason) { state.error = reason instanceof Error ? reason.message : '布局还原失败。'; }
  }
  const { editing, error, notice } = toRefs(state);
  return {
    layout: computed(() => store.state.interface),
    layoutCards: computed(() => state.editing ? selected() : null),
    layoutOpen: editing, layoutBusy: computed(() => owner.value?.state.unknown || false),
    layoutError: computed(() => error.value || owner.value?.state.error || ''), layoutNotice: notice,
    cards: computed(selected), beginLayout: begin, cancelLayout: cancel, closeLayout: close,
    saveLayout: async () => { try { await commands().settled(); close(); } catch (reason) { store.report(reason); } },
    readLayout: async () => { try { const latest = await store.inspectInterface(); commands().observe(latest); } catch (reason) { store.report(reason); } },
    changeLayout: (cards: DashboardCard[]) => void change(cards),
    resetLayout: () => void change(defaults())
  };
}
