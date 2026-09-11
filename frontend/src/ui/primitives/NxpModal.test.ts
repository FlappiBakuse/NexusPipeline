import { mount } from "@vue/test-utils";
import { h, nextTick } from "vue";
import { describe, expect, it } from "vitest";
import NxpModal from "./NxpModal.vue";

describe("NxpModal", () => {
  it("moves focus into the modal and closes from Escape or the backdrop", async () => {
    const trigger = document.createElement("button");
    document.body.append(trigger);
    trigger.focus();
    const wrapper = mount(NxpModal, { attachTo: document.body, props: { open: true, title: "测试弹窗" }, slots: { default: "内容" } });
    await nextTick();
    expect(document.activeElement).toBe(wrapper.get(".modal-close").element);
    expect(wrapper.get(".nxp-modal-backdrop").element.tagName).toBe("DIV");
    await wrapper.trigger("keydown", { key: "Escape" });
    expect(wrapper.emitted("close")).toHaveLength(1);
    await wrapper.get(".nxp-modal-backdrop").trigger("click");
    expect(wrapper.emitted("close")).toHaveLength(2);
    wrapper.unmount();
    trigger.remove();
  });

  it("keeps a locked modal open and traps Tab focus", async () => {
    const wrapper = mount(NxpModal, { attachTo: document.body, props: { open: true, title: "锁定弹窗", locked: true }, slots: { footer: () => h("button", "完成") } });
    await nextTick();
    await wrapper.trigger("keydown", { key: "Escape" });
    await wrapper.get(".nxp-modal-backdrop").trigger("click");
    expect(wrapper.emitted("close")).toBeUndefined();
    await wrapper.get(".modal-close").trigger("click");
    expect(wrapper.emitted("close")).toHaveLength(1);
    const button = wrapper.get(".modal-footer button").element as HTMLButtonElement;
    button.focus();
    await wrapper.trigger("keydown", { key: "Tab" });
    expect(document.activeElement).toBe(wrapper.get(".modal-close").element);
    wrapper.unmount();
  });
});
