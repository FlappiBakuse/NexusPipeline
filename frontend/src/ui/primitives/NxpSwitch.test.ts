import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import NxpSwitch from "./NxpSwitch.vue";
import NxpSelect from "./NxpSelect.vue";

describe("Nexus UI primitives", () => {
  it("emits the next switch value and blocks disabled interaction", async () => {
    const wrapper = mount(NxpSwitch, { props: { modelValue: false, label: "启用" } });
    await wrapper.get("button").trigger("click");
    expect(wrapper.emitted("update:modelValue")?.[0]).toEqual([true]);
    expect(wrapper.emitted("change")?.[0]).toEqual([true]);

    const disabled = mount(NxpSwitch, { props: { modelValue: false, disabled: true } });
    await disabled.get("button").trigger("click");
    expect(disabled.emitted("update:modelValue")).toBeUndefined();
  });

  it("projects options and emits the selected value", async () => {
    const wrapper = mount(NxpSelect, {
      props: {
        modelValue: "one",
        options: [
          { value: "one", label: "One" },
          { value: "two", label: "Two" },
        ],
      },
    });
    expect(wrapper.findAll("option").map(option => option.text())).toEqual(["One", "Two"]);
    await wrapper.get("select").setValue("two");
    expect(wrapper.emitted("update:modelValue")?.[0]).toEqual(["two"]);
    expect(wrapper.emitted("change")?.[0]).toEqual(["two"]);
  });
});
