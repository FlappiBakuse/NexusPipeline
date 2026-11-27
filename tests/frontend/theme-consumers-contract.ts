import { createApp, h, nextTick } from 'vue';
import AttemptLogs from '../../apps/web/src/pages/AttemptLogs.vue';
import themeCss from '../../apps/web/src/styles/theme.css?inline';
import uiCss from '../../apps/web/src/ui/theme.css?inline';
import settingsPage from '../../apps/web/src/pages/SettingsPage.vue?raw';
import { createStore } from '../../apps/web/src/app/store';
import { useAppAppearance } from '../../apps/web/src/app/app-appearance';
import { ControlClient } from '../../apps/web/src/control/client';

export async function themeConsumersContract(parent: HTMLElement, check: (value: unknown, name: string) => void) {
  const previous = document.body.dataset.theme;
  const settingsCss = /<style scoped>([\s\S]*?)<\/style>/.exec(settingsPage)?.[1];
  if (!settingsCss) throw Error('The settings warning style is missing');
  const style = document.createElement('style'); style.textContent = [themeCss, uiCss, settingsCss].join('\n');
  document.head.append(style);
  const store = createStore(new ControlClient());
  const values = { theme: 'dark' as 'dark' | 'light' | 'system', locale: 'zh-CN' as const,
    dashboardCards: [], pinnedSettings: [], collapsedSettings: [] };
  store.state.interface = { revision: 1, operationId: crypto.randomUUID(), values };
  const holder = document.createElement('section'); parent.append(holder);
  const app = createApp({ setup() {
    useAppAppearance(store);
    return () => h('div', [h(AttemptLogs, { store, runId: 'run', attemptId: 'attempt', visible: true, logs: [] }),
      h('p', { class: 'warning' }, 'Warning')]);
  } });
  app.mount(holder);
  try {
    const toggle = holder.querySelector<HTMLElement>('.history-log-toggle')!;
    toggle.click(); await nextTick();
    for (const mode of ['dark', 'light', 'system'] as const) {
      store.state.interface = { ...store.state.interface!, revision: store.state.interface!.revision + 1,
        values: { ...values, theme: mode } }; await nextTick();
      const light = document.body.dataset.theme === 'light';
      const log = getComputedStyle(holder.querySelector('.history-log-preview')!);
      check(log.backgroundColor === (light ? 'rgb(244, 246, 249)' : 'rgb(10, 18, 32)'),
        mode + ' script log uses its paired theme background');
      check(log.color === (light ? 'rgb(54, 66, 82)' : 'rgb(185, 213, 244)'),
        mode + ' script log retains readable foreground');
      check(getComputedStyle(holder.querySelector('.warning')!).borderLeftColor === (light ? 'rgb(166, 106, 11)' : 'rgb(246, 191, 97)'),
        mode + ' settings warning uses the active warning palette');
    }
  } finally {
    app.unmount(); holder.remove(); store.dispose(); style.remove();
    if (previous === undefined) delete document.body.dataset.theme; else document.body.dataset.theme = previous;
  }
}
