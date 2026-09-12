import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import NxpCollapsibleCard from "./NxpCollapsibleCard.vue";

describe("NxpCollapsibleCard", () => {
  it("keeps the body hidden until expanded and emits toggle on activation", async () => {
    const wrapper = mount(NxpCollapsibleCard, {
      props: { title: "折叠标题", description: "折叠说明", panelId: "panel-a" },
      slots: { default: "<p class=\"body-content\">内容</p>" },
    });

    const toggle = wrapper.get(".nxp-collapsible-card-toggle");
    expect(toggle.attributes("aria-expanded")).toBe("false");
    expect(toggle.attributes("aria-controls")).toBe("panel-a");
    expect(toggle.attributes("data-action")).toBe("toggle-settings-panel");
    expect(toggle.attributes("data-panel")).toBe("panel-a");
    expect(wrapper.get("#panel-a").isVisible()).toBe(false);

    await toggle.trigger("click");
    expect(wrapper.emitted("toggle")).toEqual([[true]]);
  });

  it("renders an expanded body and requests collapse", async () => {
    const wrapper = mount(NxpCollapsibleCard, {
      props: { title: "折叠标题", expanded: true, panelId: "panel-b" },
      slots: { default: "<p class=\"body-content\">内容</p>" },
    });

    expect(wrapper.get(".nxp-collapsible-card").classes()).toContain("is-expanded");
    expect(wrapper.get("#panel-b").isVisible()).toBe(true);
    expect(wrapper.get(".body-content").text()).toBe("内容");

    await wrapper.get(".nxp-collapsible-card-toggle").trigger("click");
    expect(wrapper.emitted("toggle")).toEqual([[false]]);
  });

  it("renders caller-provided header copy through the header slot", () => {
    const wrapper = mount(NxpCollapsibleCard, {
      slots: { header: "<strong class=\"custom-head\">自定义标题</strong>", default: "内容" },
    });

    expect(wrapper.get(".settings-card-copy .custom-head").text()).toBe("自定义标题");
    expect(wrapper.find(".settings-card-title").exists()).toBe(false);
  });

  it("renders side controls through the actions slot", () => {
    const wrapper = mount(NxpCollapsibleCard, {
      props: { title: "标题" },
      slots: { actions: "<button class=\"ghost side-action\">操作</button>", default: "内容" },
    });

    expect(wrapper.get(".nxp-collapsible-card-side .side-action").text()).toBe("操作");
  });
});
