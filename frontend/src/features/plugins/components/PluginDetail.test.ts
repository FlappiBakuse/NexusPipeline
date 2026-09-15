import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import PluginDetail from "./PluginDetail.vue";

describe("PluginDetail README fallback", () => {
  it("keeps cached Markdown visible while reporting a README refresh error", () => {
    const wrapper = mount(PluginDetail, {
      props: {
        tab: "store",
        detailVisibleMobile: false,
        loading: false,
        error: "",
        fetchedAt: "",
        refreshing: false,
        plugin: {
          name: "demo-plugin",
          displayName: "示例插件",
          kind: "managed-code",
          version: "1.0.0",
          status: "installed",
          installed: true,
          hasReadme: true,
          readmeAvailable: true,
          readmeMarkdown: "# Cached README\n\nThis content remains available.",
          readmeErrorCode: "plugin_readme_error",
          changelog: [],
          authors: [],
          tags: [],
        },
      },
    });

    expect(wrapper.get(".callout-warning").text()).toContain("README");
    expect(wrapper.get(".plugin-readme").text()).toContain("Cached README");
    wrapper.unmount();
  });
});
