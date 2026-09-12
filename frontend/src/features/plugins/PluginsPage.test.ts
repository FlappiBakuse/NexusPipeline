import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent } from "vue";

/** 插件页动作后把「需要重启」提升为全局状态：动作响应决定是否标记。 */

const responses = new Map<string, unknown>();
const requests: Array<{ method: string; path: string }> = [];

vi.mock("../../platform/shell", () => ({ setTopbarTitle: vi.fn() }));
vi.mock("../../platform/api", () => ({
  api: async (method: string, path: string) => {
    requests.push({ method, path });
    if (responses.has(`${method} ${path}`)) return responses.get(`${method} ${path}`);
    if (path === "/api/plugins") return [{ name: "demo-plugin", displayName: "示例插件", kind: "managed-code", configuredEnabled: true }];
    if (path.endsWith("/detail")) return { name: "demo-plugin", displayName: "示例插件", kind: "managed-code", configuredEnabled: true, hasReadme: false };
    return null;
  },
  isAbortError: () => false,
}));

import PluginsPage from "./PluginsPage.vue";
import PluginDetail from "./components/PluginDetail.vue";
import { useShellStore } from "../../stores/shell";

const Page = defineComponent({
  components: { PluginsPage },
  template: `<PluginsPage />`,
});

async function mountPage() {
  const wrapper = mount(Page, { attachTo: document.body });
  await flushPromises();
  await flushPromises();
  return wrapper;
}

describe("plugins page restart signalling", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    responses.clear();
    requests.length = 0;
  });

  it("marks a restart once an action reports it", async () => {
    responses.set("POST /api/plugins/demo-plugin/disable", { ok: true, restartRequired: true });
    const shell = useShellStore();
    const wrapper = await mountPage();

    wrapper.getComponent(PluginDetail).vm.$emit("action", "disable");
    await flushPromises();

    expect(requests).toContainEqual({ method: "POST", path: "/api/plugins/demo-plugin/disable" });
    expect(shell.restartRequired).toBe(true);
    expect(shell.restartReasons).toEqual(["plugin.disable"]);
    wrapper.unmount();
  });

  it("keeps the notice off when an action does not require a restart", async () => {
    responses.set("POST /api/plugins/demo-plugin/enable", { ok: true });
    const shell = useShellStore();
    const wrapper = await mountPage();

    wrapper.getComponent(PluginDetail).vm.$emit("action", "enable");
    await flushPromises();

    expect(shell.restartRequired).toBe(false);
    expect(shell.restartReasons).toEqual([]);
    wrapper.unmount();
  });

  it("marks a restart when the batch update changed at least one plugin", async () => {
    responses.set("POST /api/plugins/store/update-all", { ok: true, updated: [{ name: "demo-plugin", version: "1.0.1" }], failed: [], restartRequired: true });
    const shell = useShellStore();
    const wrapper = await mountPage();

    wrapper.getComponent(PluginDetail).vm.$emit("updateAll");
    await flushPromises();

    expect(shell.restartRequired).toBe(true);
    expect(shell.restartReasons).toEqual(["plugins.update-all"]);
    wrapper.unmount();
  });

  it("keeps the notice off when the batch update failed for every candidate", async () => {
    responses.set("POST /api/plugins/store/update-all", { ok: true, updated: [], failed: [{ name: "demo-plugin", code: "download_failed" }], restartRequired: false });
    const shell = useShellStore();
    const wrapper = await mountPage();

    wrapper.getComponent(PluginDetail).vm.$emit("updateAll");
    await flushPromises();

    expect(shell.restartRequired).toBe(false);
    wrapper.unmount();
  });
});
