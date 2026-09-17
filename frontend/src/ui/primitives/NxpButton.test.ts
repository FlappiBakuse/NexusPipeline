import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import NxpButton from "./NxpButton.vue";

describe("NxpButton async state", () => {
  it("exposes a busy state, locks the button, and keeps its label visible", () => {
    const wrapper = mount(NxpButton, {
      props: { busy: true },
      slots: { default: "Save" },
    });
    const button = wrapper.get("button");

    expect(button.attributes("aria-busy")).toBe("true");
    expect(button.attributes("disabled")).toBeDefined();
    expect(button.classes()).toContain("busy");
    expect(button.text()).toBe("Save");
  });

  it("does not expose the busy state when idle", () => {
    const wrapper = mount(NxpButton, { slots: { default: "Save" } });
    const button = wrapper.get("button");

    expect(button.attributes("aria-busy")).toBeUndefined();
    expect(button.attributes("disabled")).toBeUndefined();
    expect(button.classes()).not.toContain("busy");
  });

  it("applies the primary tone styling", () => {
    const wrapper = mount(NxpButton, {
      props: { tone: "primary" },
      slots: { default: "Add" },
    });

    expect(wrapper.get("button").classes()).toContain("primary");
  });
});
