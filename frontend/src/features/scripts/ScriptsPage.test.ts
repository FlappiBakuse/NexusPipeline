import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { defineComponent } from "vue";

/** 脚本页 chooser 的关闭入口契约：右上角关闭与「取消」都落到 `chooserOpen = false`。 */

vi.mock("../../platform/shell", () => ({ setTopbarTitle: vi.fn() }));
vi.mock("@bridge/index", () => ({
  renderPluginSlot: vi.fn(),
  disposePluginSlot: vi.fn(),
}));
vi.mock("./services/scriptsApi", () => ({
  listScripts: () => Promise.resolve([]),
  getStatus: () => Promise.resolve({
    plugins: [{ name: "hoyolab", displayName: "HoYoLab", kind: "data-specialized", configuredEnabled: true, runtimeEnabled: true, state: "Active" }],
  }),
  deleteScript: vi.fn(),
  reorderScripts: vi.fn(),
}));

import ScriptsPage from "./ScriptsPage.vue";

const Page = defineComponent({
  components: { ScriptsPage },
  template: `<ScriptsPage />`,
});

async function openChooser() {
  const wrapper = mount(Page, { attachTo: document.body });
  await flushPromises();
  (wrapper.get("[data-testid='new-script']").element as HTMLButtonElement).click();
  await flushPromises();
  return wrapper;
}

describe("scripts page chooser close contract", () => {
  it("shows the chooser with one close control", async () => {
    const wrapper = await openChooser();

    expect(wrapper.get(".modal-title").text()).toBe("scripts.new_script_instance");
    expect(wrapper.findAll(".modal-close")).toHaveLength(1);
    expect(wrapper.get(".modal-close").attributes("aria-label")).toBe("Close");
    wrapper.unmount();
  });

  it("dismisses the chooser from the close control", async () => {
    const wrapper = await openChooser();

    await wrapper.get(".modal-close").trigger("click");
    await flushPromises();
    expect(wrapper.find(".modal-title").exists()).toBe(false);
    wrapper.unmount();
  });

  it("dismisses the chooser from the cancel button", async () => {
    const wrapper = await openChooser();

    const cancel = wrapper.findAll(".modal-footer button").find(button => button.text() === "common.cancel");
    expect(cancel).toBeTruthy();
    await cancel!.trigger("click");
    await flushPromises();
    expect(wrapper.find(".modal-title").exists()).toBe(false);
    wrapper.unmount();
  });
});
