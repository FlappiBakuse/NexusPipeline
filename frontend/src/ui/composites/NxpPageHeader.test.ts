import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import NxpPageHeader from "./NxpPageHeader.vue";

describe("NxpPageHeader", () => {
  it("renders eyebrow, title, description and the actions slot", () => {
    const wrapper = mount(NxpPageHeader, {
      props: { eyebrow: "分组", title: "页面标题", description: "页面说明" },
      slots: { actions: "<button class=\"primary\">操作</button>" },
    });

    expect(wrapper.get(".eyebrow").text()).toBe("分组");
    expect(wrapper.get("h2").text()).toBe("页面标题");
    expect(wrapper.get(".page-kicker").text()).toBe("页面说明");
    expect(wrapper.get(".page-head-actions .primary").text()).toBe("操作");
    expect(wrapper.classes()).toContain("has-actions");
  });

  it("omits the actions column and description when the page has neither", () => {
    const wrapper = mount(NxpPageHeader, { props: { title: "仅标题" } });

    expect(wrapper.find(".page-head-actions").exists()).toBe(false);
    expect(wrapper.find(".page-kicker").exists()).toBe(false);
    expect(wrapper.classes()).not.toContain("has-actions");
  });

  it("renders title and description from slots when provided", () => {
    const wrapper = mount(NxpPageHeader, {
      slots: { eyebrow: "S", title: "<span class=\"custom-title\">插槽标题</span>", description: "插槽说明" },
    });

    expect(wrapper.get(".custom-title").text()).toBe("插槽标题");
    expect(wrapper.get(".page-kicker").text()).toBe("插槽说明");
  });
});
