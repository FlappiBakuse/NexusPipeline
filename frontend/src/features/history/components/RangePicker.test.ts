import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import RangePicker from "./RangePicker.vue";

describe("RangePicker", () => {
  it("keeps the date range surface dismissible without rendering a close button", () => {
    const wrapper = mount(RangePicker, {
      props: { open: true, from: "2026-08-14", to: "2026-09-12" },
    });

    expect(wrapper.get("[role='dialog']").attributes("aria-label")).toBe("history.choose_time_range");
    expect(wrapper.find(".nxp-dialog-popover-close").exists()).toBe(false);
  });
});
