import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";

import SettingsNetworkSection from "./SettingsNetworkSection.vue";

function mountSection() {
  const settings = {
    proxyMode: "http",
    proxyUrl: "",
    proxyUsername: "",
  };
  const secretDraft: Record<string, string> = {};
  const save = vi.fn();
  const wrapper = mount(SettingsNetworkSection, {
    props: {
      settings,
      expanded: true,
      save,
      secretDraft,
    },
  });
  return { wrapper, settings, secretDraft, save };
}

describe("SettingsNetworkSection", () => {
  it("keeps the public collapsible contract and proxy save behavior", async () => {
    const { wrapper, settings, save } = mountSection();
    const toggle = wrapper.get(".nxp-collapsible-card-toggle");

    expect(wrapper.get("[data-settings-panel=network]").attributes("data-settings-panel")).toBe("network");
    expect(toggle.attributes("aria-expanded")).toBe("true");
    expect(toggle.attributes("aria-controls")).toBe("settings-panel-network");
    expect(wrapper.get("#settings-panel-network").isVisible()).toBe(true);

    await toggle.trigger("click");
    expect(wrapper.emitted("toggle")).toEqual([[]]);
    await wrapper.setProps({ expanded: false });
    expect(toggle.attributes("aria-expanded")).toBe("false");
    expect(wrapper.get(".nxp-collapsible-card").classes()).not.toContain("nxp-collapsible-card-open");

    const url = wrapper.get("#st-proxy-url");
    await url.setValue("http://127.0.0.1:7890");
    await url.trigger("blur");

    expect(settings.proxyUrl).toBe("http://127.0.0.1:7890");
    expect(save).toHaveBeenCalledTimes(1);
    wrapper.unmount();
  });

  it("saves proxy mode through the public select control", async () => {
    const { wrapper, settings, save } = mountSection();
    const trigger = wrapper.get("#st-proxy-mode-trigger");

    await trigger.trigger("click");
    const menuId = trigger.attributes("aria-controls");
    const option = document.querySelector<HTMLButtonElement>(`#${menuId} [data-nxp-select-option][data-value="none"]`);
    expect(option).toBeTruthy();
    option?.click();
    await wrapper.vm.$nextTick();

    expect(settings.proxyMode).toBe("none");
    expect(save).toHaveBeenCalledTimes(1);
    wrapper.unmount();
  });
});
