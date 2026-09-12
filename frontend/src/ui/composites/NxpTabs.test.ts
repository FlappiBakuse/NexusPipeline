import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import NxpTabs from "./NxpTabs.vue";

const tabs = [
  { value: "local", label: "本地插件", testId: "plugin-local-tab" },
  { value: "store", label: "插件仓库", testId: "plugin-store-tab" },
];

describe("NxpTabs", () => {
  it("exposes tablist semantics with the selected tab marked", () => {
    const wrapper = mount(NxpTabs, { props: { modelValue: "local", tabs, ariaLabel: "插件视图", testId: "plugin-view-tabs" } });

    expect(wrapper.get("[role='tablist']").attributes("aria-label")).toBe("插件视图");
    expect(wrapper.find("[data-testid='plugin-view-tabs']").exists()).toBe(true);
    const buttons = wrapper.findAll("[role='tab']");
    expect(buttons).toHaveLength(2);
    expect(buttons[0].attributes("aria-selected")).toBe("true");
    expect(buttons[0].attributes("data-testid")).toBe("plugin-local-tab");
    expect(buttons[0].classes()).toContain("primary");
    expect(buttons[1].attributes("aria-selected")).toBe("false");
    expect(buttons[1].classes()).toContain("tertiary");
  });

  it("emits the new value once when another tab is activated", async () => {
    const wrapper = mount(NxpTabs, { props: { modelValue: "local", tabs } });

    await wrapper.findAll("[role='tab']")[1].trigger("click");
    expect(wrapper.emitted("update:modelValue")).toEqual([["store"]]);
    expect(wrapper.emitted("change")).toEqual([["store"]]);
  });

  it("does not emit when the active or a disabled tab is activated", async () => {
    const wrapper = mount(NxpTabs, {
      props: { modelValue: "local", tabs: [tabs[0], { ...tabs[1], disabled: true }] },
    });

    await wrapper.findAll("[role='tab']")[0].trigger("click");
    await wrapper.findAll("[role='tab']")[1].trigger("click");
    expect(wrapper.emitted("change")).toBeUndefined();
  });
});
