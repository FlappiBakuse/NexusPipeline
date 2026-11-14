import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { AppStore } from './store';
import { locale, t } from './locale';
import { interfaceSettings } from './interface-settings';
import { desktop } from './desktop';

export function useAppAppearance(store: AppStore) {
  const theme = ref<'dark' | 'light'>('dark');
  const themeBusy = ref(false);
  const themeNotice = ref('');
  const system = matchMedia('(prefers-color-scheme: light)');
  let timer: ReturnType<typeof setTimeout> | undefined;
  const themeIcon = computed(
    () => ({ dark: 'moon', light: 'sun', system: 'system' })[store.state.interface?.values.theme || 'dark']
  );
  function apply(): void {
    const mode = store.state.interface?.values.theme || 'dark';
    theme.value = mode === 'system' ? (system.matches ? 'light' : 'dark') : mode;
  }
  watch(
    theme,
    (value) => {
      document.body.dataset.theme = value;
    },
    { immediate: true }
  );
  watch(() => store.state.interface, apply, { immediate: true });
  watch(
    () => store.state.interface?.values.locale,
    (value) => {
      locale.value = value || 'zh-CN';
      document.documentElement.lang = locale.value;
      if (value) void desktop?.setLocale(value).catch(store.report);
    },
    { immediate: true }
  );
  async function toggleTheme(): Promise<void> {
    const current = store.state.interface;
    if (!current || themeBusy.value || store.interfaceChange.pending || !store.can('settings.write')) return;
    themeBusy.value = true;
    try {
      const mode = ({ system: 'light', light: 'dark', dark: 'system' } as const)[current.values.theme];
      await interfaceSettings(store).patch({ theme: mode });
      themeNotice.value = t('主题：') + t({ system: '跟随系统', light: '浅色', dark: '深色' }[mode]);
      clearTimeout(timer);
      timer = setTimeout(() => {
        themeNotice.value = '';
      }, 3000);
    } catch (error) {
      store.report(error);
    } finally {
      themeBusy.value = false;
    }
  }
  onMounted(() => system.addEventListener('change', apply));
  onBeforeUnmount(() => {
    clearTimeout(timer);
    system.removeEventListener('change', apply);
  });
  return { theme, themeBusy, themeNotice, themeIcon, toggleTheme };
}
