import { onMounted, onUpdated, ref, watch, type Ref } from 'vue';
import { locale } from '../app/locale';

export function useFormLabel(root: Ref<HTMLElement | undefined>, attrs: Record<string, unknown>) {
  const label = ref('');
  function refresh(): void {
    label.value = String(attrs['aria-label'] || root.value?.closest('label')?.textContent?.trim() || '');
  }
  onMounted(refresh);
  onUpdated(refresh);
  watch(locale, refresh, { flush: 'post' });
  return label;
}
