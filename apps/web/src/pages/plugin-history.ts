import { onBeforeUnmount, ref, watch } from 'vue';
import type { RecordsStore } from '../app/store-ports';
import { arrayValue, objectValue } from '../control/client';

export function usePluginHistory(store: RecordsStore, visible: () => boolean, pending: () => unknown) {
  const history = ref<Record<string, unknown>[]>([]),
    historyNext = ref<number | null>(null),
    historyBusy = ref(false);
  let historyOwner: AbortController | undefined;
  async function loadHistory(reset = true): Promise<void> {
    historyOwner?.abort();
    if (!visible() || !store.can('operations.read')) return;
    const offset = reset ? 0 : historyNext.value;
    if (offset === null) return;
    const controller = new AbortController();
    historyOwner = controller;
    historyBusy.value = true;
    try {
      const value = await store.client.read(
        '/control/v1/extensions/operations?offset=' + offset + '&limit=50',
        objectValue,
        controller.signal
      );
      controller.signal.throwIfAborted();
      history.value = reset
        ? arrayValue(value.items, objectValue)
        : [...history.value, ...arrayValue(value.items, objectValue)];
      historyNext.value = value.nextOffset == null ? null : Number(value.nextOffset);
    } catch (reason) {
      if (!controller.signal.aborted) store.report(reason);
    } finally {
      if (historyOwner === controller) historyBusy.value = false;
    }
  }
  watch(
    visible,
    (active) => {
      if (active) void loadHistory();
      else historyOwner?.abort();
    },
    { immediate: true }
  );
  watch(pending, (next, before) => {
    if (!next && before && visible()) void loadHistory();
  });
  onBeforeUnmount(() => historyOwner?.abort());
  watch(() => store.state.preparationRevision, () => {
    if (visible() && !pending()) void loadHistory();
  });
  return { history, historyNext, historyBusy, loadHistory };
}
