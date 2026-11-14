import { computed, watch } from 'vue';
import type { AppStore } from '../app/store';
import type { InterfaceCategory } from '../control/interface';
import { interfaceSettings } from '../app/interface-settings';

export function useSettingsView(store: AppStore, categories: readonly InterfaceCategory[]) {
  const writer = interfaceSettings(store), state = writer.state;
  const expanded = computed(() => {
    const values = state.values, preferred = values.pinnedSettings[0] || 'service';
    return !values.collapsedSettings.includes(preferred) ? preferred :
      categories.find(id => !values.collapsedSettings.includes(id)) || null;
  });
  const pinned = computed(() => state.values.pinnedSettings.filter(id => id === expanded.value));
  watch(() => store.state.interface, value => { if (value) writer.observe(value); });
  const patch = (values: Partial<typeof state.values>) => {
    if (store.can('settings.write')) void writer.patch(values).catch(store.report);
    else state.values = { ...state.values, ...values };
  };
  const isExpanded = (id: InterfaceCategory) => expanded.value === id;
  return { state, pinned, isExpanded,
    choose(id: InterfaceCategory) {
      const closing = isExpanded(id) && state.values.pinnedSettings[0] === id;
      patch({ pinnedSettings: closing ? [] : [id], collapsedSettings: categories.filter(item => closing || item !== id) });
    },
    toggleCard(id: InterfaceCategory) {
      const selected = isExpanded(id) ? null : id;
      patch({ pinnedSettings: [], collapsedSettings: categories.filter(item => item !== selected) });
    },
    setLocale(value: typeof state.values.locale) { patch({ locale: value }); }
  };
}
