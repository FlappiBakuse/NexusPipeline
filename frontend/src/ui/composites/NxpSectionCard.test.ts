import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import NxpSectionCard from "./NxpSectionCard.vue";

describe("NxpSectionCard", () => {
  it("renders the primary surface with header, description and actions", () => {
    const wrapper = mount(NxpSectionCard, {
      props: { title: "分区标题", description: "分区说明" },
      slots: { default: "<p class=\"body-content\">内容</p>", actions: "<button class=\"primary\">保存</button>" },
    });

    expect(wrapper.get(".nxp-section-card").classes()).toContain("is-primary");
    expect(wrapper.get(".nxp-section-card-title").text()).toBe("分区标题");
    expect(wrapper.get(".nxp-section-card-description").text()).toBe("分区说明");
    expect(wrapper.get(".nxp-section-card-actions .primary").text()).toBe("保存");
    expect(wrapper.get(".nxp-section-card-body .body-content").text()).toBe("内容");
  });

  it("renders the secondary surface without a header when the card carries copy only", () => {
    const wrapper = mount(NxpSectionCard, { props: { variant: "secondary" }, slots: { default: "纯内容" } });

    expect(wrapper.get(".nxp-section-card").classes()).toContain("is-secondary");
    expect(wrapper.classes()).not.toContain("has-header");
    expect(wrapper.find(".nxp-section-card-header").exists()).toBe(false);
    expect(wrapper.text()).toContain("纯内容");
  });

  it("accepts a custom header through the header slot", () => {
    const wrapper = mount(NxpSectionCard, { slots: { header: "<div class=\"custom-head\">自定义头部</div>", default: "内容" } });

    expect(wrapper.get(".custom-head").text()).toBe("自定义头部");
    expect(wrapper.find(".nxp-section-card-title").exists()).toBe(false);
  });
});
