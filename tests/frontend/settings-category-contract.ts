import { createApp, h, nextTick, reactive, ref } from 'vue';
import type { UiElement } from '@nexuspipeline/extension-sdk';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient } from '../../apps/web/src/control/client';
import type { InterfaceCategory } from '../../apps/web/src/control/interface';
import { useSettingsView, useSettingsCategories } from '../../apps/web/src/pages/settings-view';

export async function settingsCategoryContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const store = createStore(new ControlClient());
  store.state.capabilities = { instanceId: 'settings-fixture', hostVersion: '0.18.0', capabilities: [] };
  store.state.interface = { revision: 0, operationId: '', values: { theme: 'dark', locale: 'zh-CN',
    dashboardCards: ['status'], pinnedSettings: ['remote'], collapsedSettings: ['remote'] } };
  const mount = document.createElement('section'); fixture.append(mount);
  let owner!: ReturnType<typeof useSettingsCategories>;
  let expanded!: (id: InterfaceCategory) => boolean;
  const app = createApp({ setup() {
    const root = ref<HTMLElement>();
    const draft = ref('unsaved wallpaper choice');
    const ids = reactive<InterfaceCategory[]>(['service', 'remote']);
    const view = reactive(useSettingsView(store, ids));
    owner = useSettingsCategories(root, ids, view); expanded = view.isExpanded;
    return () => h('main', { ref: root, style: 'display:grid;gap:16px', 'onNexus-toggle': owner.toggle }, [
      h('section', { id: 'settings-service', style: { order: owner.order('service') } }, [h('button',
        { 'aria-expanded': owner.isExpanded('service'), onClick: () => owner.toggleCard('service') }, 'Service')]),
      h('section', { id: 'settings-remote', style: { order: owner.order('remote') } }, [h('button',
        { 'aria-expanded': owner.isExpanded('remote'), onClick: () => owner.toggleCard('remote') }, 'Remote')]),
      h('div', { id: 'settings-extension-panels', style: 'display:contents' }, [h('div', { 'data-extension-id': 'custom-wallpaper' }, [
        h('nexus-collapsible-card', { panelId: 'wallpaper', title: '自定义壁纸' }, [h('input', { value: draft.value, onInput: (event: Event) => { draft.value = (event.target as HTMLInputElement).value; } })])
      ])])
    ]);
  } });
  app.mount(mount);
  try {
    await nextTick();
    const category = 'extension:custom-wallpaper:wallpaper' as const;
    const card = () => mount.querySelector('nexus-collapsible-card') as UiElement<'nexus-collapsible-card'>;
    const top = (element: Element) => element.getBoundingClientRect().top;
    const serviceCard = mount.querySelector('#settings-service')!;
    const remoteCard = mount.querySelector('#settings-remote')!;
    const remoteExpanded = () => remoteCard.querySelector('button')?.getAttribute('aria-expanded') === 'true';
    const pluginCard = () => card().closest('[data-extension-id]')!;
    const normalOrder = () => top(serviceCard) < top(remoteCard) && top(remoteCard) < top(pluginCard());
    check(expanded('service') && !card().expanded, 'the host settings category is initially the only expanded category');
    check(top(serviceCard) < top(remoteCard), 'a previously pinned collapsed category does not precede the active category');
    owner.toggleCard('remote'); await nextTick();
    check(normalOrder() && expanded('remote'), 'a host heading expands in place without pinning');
    owner.toggleCard('remote'); await nextTick();
    check(normalOrder() && !expanded('remote'), 'a host heading collapses in place');
    const remote = owner.choose('remote'); await nextTick();
    check(top(remoteCard) < top(serviceCard) && top(remoteCard) < top(pluginCard()) && !remoteExpanded(),
      'a category button places the collapsed card first before expansion');
    await remote;
    check(remoteExpanded(), 'the category button expands after placement');
    owner.toggleCard(category); await nextTick();
    check(card().expanded && !expanded('remote') && normalOrder(),
      'a contributed heading expands without pinning and clears the collapsed category pin');
    card().shadowRoot!.querySelector<HTMLButtonElement>('button')!.click(); await nextTick();
    check(!card().expanded && normalOrder(), 'a contributed heading collapses without changing order');
    await owner.choose(category);
    check(card().expanded && !expanded('service') && top(pluginCard()) < top(serviceCard) && top(pluginCard()) < top(remoteCard),
      'a contributed public settings card shares the host expansion owner and moves ahead of both host cards');
    await owner.choose(category);
    check(!card().expanded && !expanded('service') && normalOrder(),
      'closing the pinned contributed category restores its visual order');
    card().querySelector('input')!.value = 'changed wallpaper draft';
    card().querySelector('input')!.dispatchEvent(new Event('input', { bubbles: true }));
    await nextTick();
    const superseded = owner.choose(category); const latest = owner.choose('remote');
    await Promise.all([superseded, latest]);
    check(!card().expanded && expanded('remote') && card().querySelector('input')?.value === 'changed wallpaper draft',
      'switching back to a host category keeps the plugin draft and one expanded category');
  } finally { app.unmount(); mount.remove(); store.dispose(); }
}
