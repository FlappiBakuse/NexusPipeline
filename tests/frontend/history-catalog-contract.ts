import { createApp, h, nextTick, ref } from 'vue';
import HistoryCatalogWindow from '../../apps/web/src/components/HistoryCatalogWindow.vue';

export async function historyCatalogContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const items = ref(Array.from({ length: 10000 }, (_, index) => ({ id: String(index) })));
  const visible = ref(true), selected = ref('');
  const mount = document.createElement('section');
  fixture.append(mount);
  const app = createApp({ render: () => h(HistoryCatalogWindow, {
    items: items.value, itemKey: (item: { id: string }) => item.id, label: 'Long history catalog', visible: visible.value
  }, { default: ({ item }: { item: { id: string } }) => h('button', {
    onClick: () => { selected.value = item.id; }
  }, 'Log ' + item.id) }) });
  app.mount(mount);
  const settle = async () => { await nextTick(); await new Promise(resolve => requestAnimationFrame(resolve)); await nextTick(); };
  try {
    await settle();
    const viewport = mount.querySelector<HTMLElement>('nexus-scroll-area')!;
    const bounded = () => check(mount.querySelectorAll('button').length < 30,
      'ten thousand loaded entries retain a bounded mounted catalog');
    bounded();
    const first = mount.querySelector<HTMLButtonElement>('button')!;
    first.focus(); first.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true, cancelable: true }));
    await settle();
    check(document.activeElement?.textContent === 'Log 9999', 'keyboard End reaches the final loaded entry');
    (document.activeElement as HTMLButtonElement).click();
    check(selected.value === '9999', 'selection retains the original entry identity at the end of a long catalog');
    bounded();
    const position = viewport.scrollTop;
    items.value = [...items.value, ...Array.from({ length: 200 }, (_, index) => ({ id: String(index + 10000) }))];
    await settle();
    check(viewport.scrollTop === position && selected.value === '9999', 'appending a page preserves position and selection');
    viewport.scrollTop = 5000 * 44; viewport.dispatchEvent(new Event('scroll')); await settle();
    check([...mount.querySelectorAll('button')].some(item => item.textContent === 'Log 5000'), 'scrolling reaches a middle catalog entry');
    bounded();
    visible.value = false; await settle();
    check(mount.querySelectorAll('button').length === 0, 'hidden catalogs release mounted rows');
    visible.value = true; await settle(); bounded();
    items.value = [{ id: 'replacement' }]; await settle();
    check(viewport.scrollTop === 0 && mount.querySelector('button')?.textContent === 'Log replacement',
      'changing the catalog resets its scroll position without retaining old rows');
  } finally { app.unmount(); mount.remove(); }
}
