import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { ConfigurationStore } from '../app/store-ports';
import { extensionList, extensionDetails, type ExtensionState, type ExtensionDetails } from '../control/extensions';

export function useAdapterAvailability(
  store: ConfigurationStore,
  extensionId: () => string | null,
  visible: () => boolean,
  unavailable: (reason?: unknown) => void
) {
  const extension = ref<ExtensionState | null>(null);
  const details = ref<ExtensionDetails>();
  const loadingExtension = ref(false);
  const available = computed(
    () =>
      extension.value?.isInstalled === true &&
      extension.value?.desiredEnabled === true &&
      !!extension.value?.activationId &&
      Number(extension.value?.installedRevision) > 0
  );
  let owner: AbortController | undefined;
  watch(
    [visible, extensionId],
    async ([shown, id]) => {
      owner?.abort();
      extension.value = null;
      details.value = undefined;
      if (!shown || !id) {
        loadingExtension.value = false;
        return;
      }
      const current = new AbortController();
      owner = current;
      loadingExtension.value = true;
      try {
        const result = await store.client.read('/control/v1/extensions', extensionList, current.signal);
        current.signal.throwIfAborted();
        extension.value =
          result.items.find((item) => item.extensionId === id) || null;
        if (!available.value) unavailable();
        else details.value = await store.client.read('/control/v1/extensions/' + encodeURIComponent(id) + '/details',
          value => extensionDetails(value, id), current.signal);
      } catch (error) {
        if (!current.signal.aborted) unavailable(error);
      } finally {
        if (owner === current) loadingExtension.value = false;
      }
    },
    { immediate: true }
  );
  onBeforeUnmount(() => owner?.abort());
  return { extension, details, loadingExtension, available };
}
