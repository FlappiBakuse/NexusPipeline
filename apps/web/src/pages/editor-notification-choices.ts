import { onBeforeUnmount, ref, watch } from 'vue';
import type { ConfigurationStore } from '../app/store-ports';
import { notificationCatalog, type NotificationTask } from './notification-types';

export function useEditorNotifications(store: ConfigurationStore, report: (reason: unknown) => void) {
  const notifications = ref<NotificationTask[]>([]);
  let owner: AbortController | undefined;
  watch([() => store.state.capabilities?.instanceId, () => store.can('notifications.read')], async ([instance, allowed]) => {
    owner?.abort();
    notifications.value = [];
    if (!instance || !allowed) return;
    const current = new AbortController();
    owner = current;
    try {
      const catalog = await store.readEditorDirectory('notifications', notificationCatalog, current.signal);
      current.signal.throwIfAborted();
      notifications.value = catalog.tasks;
    } catch (reason) {
      if (!current.signal.aborted) report(reason);
    }
  }, { immediate: true });
  onBeforeUnmount(() => owner?.abort());
  return notifications;
}
