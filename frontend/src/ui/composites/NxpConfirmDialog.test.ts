import { mount } from "@vue/test-utils";
import { nextTick } from "vue";
import { describe, expect, it } from "vitest";
import NxpConfirmDialog from "./NxpConfirmDialog.vue";

describe("NxpConfirmDialog", () => {
  it("emits cancel and confirm actions with the supplied labels", async () => {
    const wrapper = mount(NxpConfirmDialog, {
      attachTo: document.body,
      props: {
        open: true,
        title: "删除项目",
        message: "确认删除？",
        confirmLabel: "删除",
        cancelLabel: "返回",
        confirmTone: "danger",
      },
    });
    await nextTick();

    expect(wrapper.get("[role='dialog']").attributes("aria-label")).toBe("删除项目");
    expect(wrapper.get(".nxp-confirm-message").text()).toBe("确认删除？");
    expect(wrapper.get(".modal-footer .ghost").text()).toBe("返回");
    expect(wrapper.get(".modal-footer .danger").text()).toBe("删除");

    await wrapper.get(".modal-footer .ghost").trigger("click");
    await wrapper.get(".modal-footer .danger").trigger("click");

    expect(wrapper.emitted("cancel")).toHaveLength(1);
    expect(wrapper.emitted("confirm")).toHaveLength(1);
    wrapper.unmount();
  });

  it("locks both close paths while confirmation is busy", async () => {
    const wrapper = mount(NxpConfirmDialog, {
      attachTo: document.body,
      props: { open: true, title: "处理中", busy: true },
    });
    await nextTick();

    expect(wrapper.find(".modal-close").exists()).toBe(false);
    expect(wrapper.get(".modal-footer .ghost").attributes("disabled")).toBeDefined();
    expect(wrapper.get(".modal-footer .primary").attributes("disabled")).toBeDefined();
    await wrapper.get(".nxp-modal").trigger("keydown", { key: "Escape" });
    expect(wrapper.emitted("close")).toBeUndefined();
    wrapper.unmount();
  });
});
