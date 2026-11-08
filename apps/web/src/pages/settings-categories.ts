import { nextTick, onBeforeUnmount, onMounted, ref, type Ref } from 'vue';
import type { UiElement } from '@nexuspipeline/extension-sdk';
import type { InterfaceCategory } from '../control/interface';

type Card = UiElement<'nexus-collapsible-card'>;
type View = { choose(id: InterfaceCategory): void; toggleCard(id: InterfaceCategory): void; isExpanded(id: InterfaceCategory): boolean; pinned: InterfaceCategory[] };

export function useSettingsCategories(root: Ref<HTMLElement | undefined>, ids: InterfaceCategory[], view: View) {
  const extensions = ref<{ id: InterfaceCategory; label: string; element: Card }[]>([]);
  const builtins = [...ids];
  let observer: MutationObserver | undefined;
  let selection = 0;
  const opening = ref<InterfaceCategory | null>(null);
  const isExpanded = (id: InterfaceCategory): boolean => opening.value !== id && view.isExpanded(id);
  function synchronize(): void {
    if (!root.value) return;
    const cards = [...root.value.querySelectorAll<Card>('#settings-extension-panels nexus-collapsible-card')];
    const next = cards.flatMap(element => {
      const extension = element.closest<HTMLElement>('[data-extension-id]')?.dataset.extensionId;
      if (!extension || !/^[a-z0-9-]{1,80}$/.test(extension) || !/^[a-z0-9-]{1,40}$/.test(element.panelId)) return [];
      const id: InterfaceCategory = `extension:${extension}:${element.panelId}`;
      element.expanded = isExpanded(id);
      element.style.order = String(order(id));
      const container = element.closest<HTMLElement>('[data-extension-id]');
      if (container) container.style.order = String(order(id));
      return [{ id, label: element.title, element }];
    });
    if (next.map(item => item.id + item.label).join('\n') !== extensions.value.map(item => item.id + item.label).join('\n')) {
      extensions.value = next;
      ids.splice(0, ids.length, ...builtins, ...next.map(item => item.id));
    }
  }
  function order(id: InterfaceCategory): number { return view.pinned[0] === id ? 1 : 2 + ids.indexOf(id); }
  async function choose(id: InterfaceCategory): Promise<void> {
    const current = ++selection;
    const viewport = root.value?.closest<HTMLElement>('nexus-scroll-area');
    const bar = root.value?.querySelector<HTMLElement>('.categories');
    const top = bar?.getBoundingClientRect().top;
    opening.value = view.isExpanded(id) ? null : id;
    view.choose(id);
    await nextTick();
    synchronize();
    if (viewport && bar && top !== undefined)
      viewport.scrollTop += bar.getBoundingClientRect().top - top;
    // Paint the reordered, collapsed card before starting its height transition.
    await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
    await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
    if (current !== selection) return;
    opening.value = null;
    synchronize();
  }

  function toggle(event: Event): void {
    const card = event.composedPath().find((node): node is Card => node instanceof HTMLElement && node.localName === 'nexus-collapsible-card');
    const category = extensions.value.find(item => item.element === card);
    if (category) { event.stopPropagation(); toggleCard(category.id); }
  }
  function toggleCard(id: InterfaceCategory): void {
    selection++;
    opening.value = null;
    view.toggleCard(id);
    synchronize();
  }
  onMounted(() => {
    observer = new MutationObserver(synchronize);
    if (root.value) observer.observe(root.value, { childList: true, subtree: true });
    synchronize();
  });
  onBeforeUnmount(() => { selection++; observer?.disconnect(); });
  return { extensions, choose, toggle, toggleCard, synchronize, order, isExpanded };
}
