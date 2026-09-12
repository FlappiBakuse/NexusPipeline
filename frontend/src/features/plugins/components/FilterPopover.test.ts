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

    const dialog = wrapper.get("[role='dialog']");
    expect(dialog.attributes("aria-label")).toBe("plugins.plugin_filters_and_sorting");
    expect(dialog.findAll("button[aria-label='common.close']")).toHaveLength(0);
  });
});
