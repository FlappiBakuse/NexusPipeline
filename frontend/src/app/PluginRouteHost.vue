<script lang="ts">
import { computed, defineComponent, h, onBeforeUnmount, onMounted, watch } from "vue";
import { useRoute } from "vue-router";
import { disposePage, enterPage, state } from "../platform/page-state";
import {
  initPluginRuntime,
  notifyPluginDispose,
  notifyPluginPageEnter,
  notifyPluginPageLeave,
  notifyPluginPageUpdated,
  resolvePluginRoute,
  syncPluginNavActive,
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

    const renderRoute = async () => {
      if (!mounted) return;
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
      const handler = resolvePluginRoute(current);
      if (!handler) {
        location.hash = "#/dashboard";
        return;
      }
      await handler(token, current);
      await notifyPluginPageEnter({ hash, page: current[0] || "plugin", segments: current, token, container: document.querySelector("#view") });
      await notifyPluginPageUpdated({ hash, page: current[0] || "plugin", segments: current, token, container: document.querySelector("#view") });
      syncPluginNavActive(location.hash || "#/dashboard");
    };

    onMounted(() => {
      mounted = true;
      void renderRoute();
    });
    onBeforeUnmount(async () => {
      mounted = false;
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

    return () => h("main", { id: "view", class: "view-root", tabindex: "-1", "data-testid": "main-view" });
  },
});
</script>
