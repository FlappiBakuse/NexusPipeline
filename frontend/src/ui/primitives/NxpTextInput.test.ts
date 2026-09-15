import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import NxpTextInput from "./NxpTextInput.vue";

describe("NxpTextInput", () => {
  it("keeps the password visibility control after focus leaves and returns", async () => {
    const wrapper = mount(NxpTextInput, {
      props: {
        modelValue: "secret-token",
        type: "password",
        showPasswordToggle: true,
        showPasswordLabel: "显示 Token",
        hidePasswordLabel: "隐藏 Token",
      },
    });

    expect(wrapper.find(".nxp-password-toggle").exists()).toBe(true);
    await wrapper.get("input").trigger("blur");
    await wrapper.get("input").trigger("focus");
    expect(wrapper.find(".nxp-password-toggle").exists()).toBe(true);

    await wrapper.get(".nxp-password-toggle").trigger("click");
    expect(wrapper.get("input").attributes("type")).toBe("text");
    expect(wrapper.get(".nxp-password-toggle").attributes("aria-pressed")).toBe("true");
    expect(wrapper.get(".nxp-password-toggle").attributes("aria-label")).toBe("隐藏 Token");

    await wrapper.setProps({ modelValue: "" });
    expect(wrapper.find(".nxp-password-toggle").exists()).toBe(false);
    expect(wrapper.get("input").attributes("type")).toBe("password");
  });
});
