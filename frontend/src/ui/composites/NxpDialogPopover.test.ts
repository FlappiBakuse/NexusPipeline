import { mount } from "@vue/test-utils";
import { nextTick } from "vue";
import { describe, expect, it } from "vitest";
import NxpDialogPopover from "./NxpDialogPopover.vue";

describe("NxpDialogPopover", () => {
  it("renders a labelled dialog with a standard close control", async () => {
    const wrapper = mount(NxpDialogPopover, {
      attachTo: document.body,
      props: { open: true, id: "filter-popover", ariaLabel: "插件筛选与排序" },
      slots: { default: "<p class=\"popover-content\">筛选内容</p>" },
    });
    await nextTick();

    const dialog = wrapper.get("[role='dialog']");
    expect(dialog.attributes("id")).toBe("filter-popover");
    expect(dialog.attributes("aria-label")).toBe("插件筛选与排序");
    expect(wrapper.get(".popover-content").text()).toBe("筛选内容");

    await wrapper.get(".nxp-dialog-popover-close").trigger("click");
    expect(wrapper.emitted("close")).toHaveLength(1);
    wrapper.unmount();
  });

  it("closes from Escape and returns focus to the opener", async () => {
    const opener = document.createElement("button");
    opener.textContent = "打开";
    document.body.append(opener);
    opener.focus();

    const wrapper = mount(NxpDialogPopover, { attachTo: document.body, props: { open: true, ariaLabel: "时间范围" }, slots: { default: "<p>内容</p>" } });
    await nextTick();
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(wrapper.emitted("close")).toHaveLength(1);

    await wrapper.setProps({ open: false });
    await nextTick();
    expect(document.activeElement).toBe(opener);

    wrapper.unmount();
    opener.remove();
  });

  it("renders nothing while closed", () => {
    const wrapper = mount(NxpDialogPopover, { props: { open: false, ariaLabel: "关闭态" }, slots: { default: "内容" } });

    expect(wrapper.find("[role='dialog']").exists()).toBe(false);
    expect(wrapper.text()).toBe("");
  });

  it("omits the close control when the surface manages its own dismissal", () => {
    const wrapper = mount(NxpDialogPopover, { props: { open: true, closeable: false, ariaLabel: "无关闭" }, slots: { default: "内容" } });

    expect(wrapper.find(".nxp-dialog-popover-close").exists()).toBe(false);
  });
});
