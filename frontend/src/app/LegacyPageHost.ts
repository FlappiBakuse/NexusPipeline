import { defineComponent, h, onBeforeUnmount, onMounted, watch } from "vue";
import { pageDashboard } from "@legacy/views/dashboard.js";
import { pageScripts } from "@legacy/views/scripts.js";
import { pageUsers } from "@legacy/views/users/index.js";
import { pageQueues } from "@legacy/views/queues.js";
import { pageDispatch } from "@legacy/views/dispatch.js";
import { pageHistory } from "@legacy/views/history.js";
import { pagePlugins } from "@legacy/views/plugins.js";
import { pageSettings } from "@legacy/views/settings.js";
import { enterPage, disposePage } from "@legacy/core/state.js";
import {
  initPluginRuntime,
  notifyPluginDispose,
  notifyPluginPageEnter,
  notifyPluginPageLeave,
  notifyPluginPageUpdated,
  resolvePluginRoute,
  syncPluginNavActive,
} from "@legacy/core/plugin-runtime.js";

const pages: Record<string, (token: number, segments?: string[]) => Promise<void>> = {
  dashboard: pageDashboard,
  scripts: pageScripts,
  users: pageUsers,
  queues: pageQueues,
  dispatch: pageDispatch,
  history: pageHistory,
  plugins: pagePlugins,
  settings: pageSettings,
};

export default defineComponent({
  name: "LegacyPageHost",
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
      const hash = segments.join("/") || "dashboard";
      const page = segments[0] || "dashboard";
      if (previousHash && previousHash !== hash) {
        const previousSegments = previousHash.split("/");
        await notifyPluginPageLeave({ hash: previousHash, page: previousSegments[0], segments: previousSegments });
        await notifyPluginDispose({ hash: previousHash, page: previousSegments[0], segments: previousSegments });
      }
      previousHash = hash;
      const token = enterPage(page);
      const handler = pages[page] || resolvePluginRoute(segments) || pageDashboard;
      await initPluginRuntime();
      await handler(token, segments);
      await notifyPluginPageEnter({ hash, page, segments, token, container: document.querySelector("#view") });
      await notifyPluginPageUpdated({ hash, page, segments, token, container: document.querySelector("#view") });
      syncPluginNavActive(location.hash || "#/dashboard");
    };

    onMounted(() => {
      mounted = true;
      void renderRoute();
    });
    onBeforeUnmount(async () => {
      mounted = false;
      if (previousHash) {
        const segments = previousHash.split("/");
        await notifyPluginPageLeave({ hash: previousHash, page: segments[0], segments });
        await notifyPluginDispose({ hash: previousHash, page: segments[0], segments });
      }
      disposePage();
    });
    watch(() => [props.ready, props.segments.join("/")], () => void renderRoute());

    return () => h("main", { id: "view", tabindex: "-1", "data-testid": "main-view" });
  },
});
