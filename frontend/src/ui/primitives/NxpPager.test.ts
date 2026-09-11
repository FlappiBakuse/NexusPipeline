import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import NxpPager from "./NxpPager.vue";

describe("NxpPager", () => {
  it("hides the pager when the collection fits on one page", () => {
    const wrapper = mount(NxpPager, {
      props: { total: 1, totalPages: 1 },
    });

    expect(wrapper.find("[data-testid='nxp-pager']").exists()).toBe(false);
  });

  it("renders a full footer row and emits page changes for multiple pages", async () => {
    const wrapper = mount(NxpPager, {
      props: {
        page: 2,
        total: 45,
        totalPages: 3,
        pageSize: 20,
        previousLabel: "上一页",
        nextLabel: "下一页",
      },
    });

    expect(wrapper.get("nav").classes()).toContain("pager");
    expect(wrapper.get(".pager-info").text()).toContain("45");
    expect(wrapper.get("[aria-current='page']").text()).toBe("2");
    expect(wrapper.findAll(".nxp-pager-controls > button")).toHaveLength(5);

    await wrapper.get(".nxp-pager-controls > button:last-child").trigger("click");
    expect(wrapper.emitted("update:page")).toEqual([[3]]);
    expect(wrapper.emitted("pageChange")).toEqual([[3]]);
  });
});
