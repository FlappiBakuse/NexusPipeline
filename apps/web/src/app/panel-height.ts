import { nextTick, onBeforeUnmount, onMounted, ref, watch, type Ref } from 'vue';

export function usePanelHeight(panel: Ref<HTMLElement | undefined>, visible: () => boolean) {
  const height = ref('600px');
  let observer: ResizeObserver | undefined;
  function measure(): void {
    const element = panel.value;
    if (!element || !visible()) return;
    const scroll = element.closest('.workspace-scroll');
    const top = element.getBoundingClientRect().top + (scroll?.scrollTop || 0);
    height.value = Math.max(240, innerHeight - top - 28) + 'px';
  }
  onMounted(() => {
    observer = new ResizeObserver(measure);
    const header = panel.value?.closest('.workspace')?.querySelector('.workspace-header');
    if (header) observer.observe(header);
    window.addEventListener('resize', measure);
    measure();
  });
  watch(visible, async (value) => {
    if (value) {
      await nextTick();
      measure();
    }
  });
  onBeforeUnmount(() => {
    observer?.disconnect();
    window.removeEventListener('resize', measure);
  });
  return { height, measure };
}
