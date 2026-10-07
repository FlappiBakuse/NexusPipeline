<script lang="ts">
import { computed, defineComponent, h, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { useRoute } from "vue-router";
import { disposePage, enterPage, state } from "../platform/page-state";
import {
  initPluginRuntime,
  notifyPluginDispose,
  notifyPluginPageEnter,
  notifyPluginPageLeave,
  notifyPluginPageUpdated,
  resolvePluginRoute,
} from "@bridge/index";

/**
 * 插件 route 的 mount、leave、dispose 生命周期边界。
 *
 * 组件只由 `/plugin/:pathMatch(.*)*` 承载，`RouterView` 在 shell boot 完成后才渲染，
 * 因此 route segment 直接取自当前路由，不再由上游额外注入 ready 门禁与 segments。
 */
export default defineComponent({
  name: "PluginRouteHost",
  setup() {
    const route = useRoute();
    const segments = computed(() =>
      String(route.path || "")
        .replace(/^\/+|\/+$/g, "")
        .split("/")
        .filter(Boolean),
    );

    let previousHash = "";
    let mounted = false;
    const container = ref<HTMLElement | null>(null);
    let surfaceController: AbortController | null = null;
    let routeGeneration = 0;

    function releaseSurface() {
      surfaceController?.abort(); surfaceController = null;
      container.value?.replaceChildren();
    }

    const renderRoute = async () => {
      if (!mounted) return;
      const generation = ++routeGeneration;
      releaseSurface();
      const current = segments.value.map(String);
      const hash = current.join("/");
      if (previousHash && previousHash !== hash) {
        const previousSegments = previousHash.split("/");
        await notifyPluginPageLeave({ hash: previousHash, page: previousSegments[0], segments: previousSegments });
        await notifyPluginDispose({ hash: previousHash, page: previousSegments[0], segments: previousSegments });
      }
      previousHash = hash;
      const token = enterPage(current[0] || "plugin");
      await initPluginRuntime();
      if (!mounted || generation !== routeGeneration || !container.value) return;
      const handler = resolvePluginRoute(current);
      if (!handler) {
        location.hash = "#/dashboard";
        return;
      }
      const element = document.createElement("div");
      container.value.append(element);
      const controller = new AbortController(); surfaceController = controller;
      try { await handler({ element, token, segments: current, signal: controller.signal }); }
      catch { if (controller.signal.aborted) return; releaseSurface(); location.hash = "#/dashboard"; return; }
      if (controller.signal.aborted || generation !== routeGeneration) return;
      await notifyPluginPageEnter({ hash, page: current[0] || "plugin", segments: current, token, container: element });
      await notifyPluginPageUpdated({ hash, page: current[0] || "plugin", segments: current, token, container: element });
    };

    onMounted(() => {
      mounted = true;
      void renderRoute();
    });
    onBeforeUnmount(async () => {
      mounted = false;
      routeGeneration++; releaseSurface();
      const previous = previousHash;
      previousHash = "";
      disposePage();
      state.page = "";
      state.routeToken += 1;
      if (previous) {
        const previousSegments = previous.split("/");
        await notifyPluginPageLeave({ hash: previous, page: previousSegments[0], segments: previousSegments });
        await notifyPluginDispose({ hash: previous, page: previousSegments[0], segments: previousSegments });
      }
    });
    watch(segments, () => void renderRoute());

    return () => h("main", { ref: container, id: "view", class: "view-root", tabindex: "-1", "data-testid": "main-view" });
  },
});
</script>
