import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import NotificationTaskSelect from "./NotificationTaskSelect.vue";
import NxpSelect from "../../ui/primitives/NxpSelect.vue";
const fixture = vi.hoisted(() => ({ load: vi.fn() }));
vi.mock("./first-screen", () => ({ loadNotificationsPage: fixture.load }));

describe("notification target loading", () => {
  it("retains selected IDs until options arrive and then enables selection", async () => {
    let resolve!: (tasks: unknown[]) => void;
    fixture.load.mockReturnValueOnce(new Promise(result => { resolve = result; }));
    const wrapper = mount(NotificationTaskSelect, { props: { modelValue: ["existing"] } });
    const select = wrapper.getComponent(NxpSelect);
    expect(select.props("disabled")).toBe(true);
    expect(select.props("modelValue")).toEqual(["existing"]);
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
    resolve([{ id: "existing", name: "Saved task", type: "smtp", enabled: true, configured: true }]);
    await flushPromises();
    expect(select.props("disabled")).toBe(false);
    expect(select.props("options")).toEqual([{ value: "existing", label: "Saved task · SMTP" }]);
    expect(select.props("modelValue")).toEqual(["existing"]);
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
    wrapper.unmount();
  });

  it("preserves selected IDs on a failed load and aborts its request on unmount", async () => {
    fixture.load.mockRejectedValueOnce(new Error("Controlled failure"));
    const wrapper = mount(NotificationTaskSelect, { props: { modelValue: ["retained"] } });
    await flushPromises();
    expect(wrapper.getComponent(NxpSelect).props("disabled")).toBe(true);
    expect(wrapper.getComponent(NxpSelect).props("modelValue")).toEqual(["retained"]);
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
    const signal = fixture.load.mock.calls.at(-1)![0] as AbortSignal;
    wrapper.unmount();
    expect(signal.aborted).toBe(true);
  });
});
