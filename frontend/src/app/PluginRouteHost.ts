import { defineComponent, h, onBeforeUnmount, onMounted, watch } from "vue";
import { enterPage, disposePage, state } from "@legacy/core/state.js";
import {
  initPluginRuntime,
  notifyPluginDispose,
  notifyPluginPageEnter,
  notifyPluginPageLeave,
  notifyPluginPageUpdated,
  resolvePluginRoute,
  syncPluginNavActive,
} from "@legacy/core/plugin-runtime.js";

export default defineComponent({
  name: "PluginRouteHost",
  props: {
    segments: { type: Array, required: true },
    ready: { type: Boolean, default: false },
  },
  setup(props) {
    let previousHash = "";
    let mounted = false;

    const renderRoute = async () => {
      if (!mounted || !props.ready) return;
      const segments = props.segments.map(String);
      const hash = segments.join("/");
      if (previousHash && previousHash !== hash) {
        const previousSegments = previousHash.split("/");
        await notifyPluginPageLeave({ hash: previousHash, page: previousSegments[0], segments: previousSegments });
        await notifyPluginDispose({ hash: previousHash, page: previousSegments[0], segments: previousSegments });
      }
      previousHash = hash;
      const token = enterPage(segments[0] || "plugin");
      await initPluginRuntime();
      const handler = resolvePluginRoute(segments);
      if (!handler) {
        location.hash = "#/dashboard";
        return;
      }
      await handler(token, segments);
      await notifyPluginPageEnter({ hash, page: segments[0] || "plugin", segments, token, container: document.querySelector("#view") });
      await notifyPluginPageUpdated({ hash, page: segments[0] || "plugin", segments, token, container: document.querySelector("#view") });
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
        const segments = previous.split("/");
        await notifyPluginPageLeave({ hash: previous, page: segments[0], segments });
        await notifyPluginDispose({ hash: previous, page: segments[0], segments });
      }
    });
    watch(() => [props.ready, props.segments.join("/")], () => void renderRoute());

    return () => h("main", { id: "view", tabindex: "-1", "data-testid": "main-view" });
  },
});
