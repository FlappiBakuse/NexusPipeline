import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";

vi.mock("../../platform/api", () => ({
  apiBlob: vi.fn().mockRejectedValue(new Error("icon unavailable")),
  isAbortError: () => false,
}));

import ScriptCard from "./ScriptCard.vue";

const script = {
  id: "script-unknown",
  name: "绝区零签到",
  pluginType: "zenless-zone-zero",
};

const translate = (key: string) => key === "common.plugin.unknown" ? "未知专项" : key;

describe("ScriptCard unavailable specialized script", () => {
  it("shows the unknown-specialized badge and keeps the edit action available for the error toast", async () => {
    const wrapper = mount(ScriptCard, {
      props: {
        script,
        pluginLabel: "绝区零",
        unavailableMessage: "专项插件未安装",
        translate,
      },
    });

    const badge = wrapper.get("[data-testid='script-card-plugin-badge']");
    expect(badge.text()).toBe("未知专项");
    expect(badge.classes()).toContain("bad");

    const edit = wrapper.findAll(".script-ops button").find(button => button.text() === "scripts.edit_script");
    expect(edit).toBeTruthy();
    expect(edit!.attributes("disabled")).toBeUndefined();
    await edit!.trigger("click");
    expect(wrapper.emitted("edit")).toHaveLength(1);

    wrapper.unmount();
  });
});
