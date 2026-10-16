<script lang="ts">
import { defineComponent, h, onBeforeUnmount, onMounted, ref } from "vue";
import { useRoute } from "vue-router";
import { state } from "../platform/page-state";
import { takePreparedData } from '../platform/navigation-preparation';
import type { PreparedPluginRoute } from './navigation';
import { notifyPluginDispose, notifyPluginPageEnter, notifyPluginPageLeave, notifyPluginPageUpdated } from "@bridge/index";

export default defineComponent({
  name: "PluginRouteHost",
  setup() {
    // Route objects update before old components unmount; leave notifications need this surface's route.
    const segments = String(useRoute().path).split('/').filter(Boolean);
    const hash = segments.join('/');
    const prepared = takePreparedData<PreparedPluginRoute>('plugin');
    if (!prepared) throw new Error('plugin_route_not_prepared');
    const token = state.routeToken;
    const container = ref<HTMLElement | null>(null);
    onMounted(async () => {
      container.value?.append(prepared.element);
      await notifyPluginPageEnter({ hash, page: 'plugin', segments, token, container: prepared.element });
      if (prepared.controller.signal.aborted) return;
      await notifyPluginPageUpdated({ hash, page: 'plugin', segments, token, container: prepared.element });
    });
    onBeforeUnmount(async () => {
      prepared.dispose();
      await notifyPluginPageLeave({ hash, page: 'plugin', segments, token });
      await notifyPluginDispose({ hash, page: 'plugin', segments, token });
    });
    return () => h("main", { ref: container, id: "view", class: "view-root", tabindex: "-1", "data-testid": "main-view" });
  },
});
</script>
