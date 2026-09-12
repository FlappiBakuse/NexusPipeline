import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import FilterPopover from "./FilterPopover.vue";

describe("FilterPopover", () => {
  it("keeps the filter surface dismissible without rendering a close button", () => {
    const wrapper = mount(FilterPopover, {
      props: {
        open: true,
        view: { query: "", kind: "all", sortBy: "name", direction: "asc" },
      },
    });

    expect(wrapper.get("[role='dialog']").attributes("aria-label")).toBe("plugins.plugin_filters_and_sorting");
    expect(wrapper.find(".nxp-dialog-popover-close").exists()).toBe(false);
  });
});
