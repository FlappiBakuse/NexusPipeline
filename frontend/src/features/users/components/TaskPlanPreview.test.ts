import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import TaskPlanPreview from "./TaskPlanPreview.vue";
const { api } = vi.hoisted(() => ({ api: vi.fn() }));
vi.mock("../../../platform/api", () => ({ api }));
vi.mock("../../../platform/i18n", () => ({ t: (key: string) => key }));
describe("task plan disclosure", () => {
  it("loads on expansion, collapses without fetching and clears another binding's plan", async () => {
    api.mockResolvedValue({ plan: { tasks: [], coverage: "partial", diagnostics: [] } });
    const wrapper = mount(TaskPlanPreview, { props: { userId: "one", scriptId: "script" } });
    expect(api).not.toHaveBeenCalled();
    const toggle = wrapper.get('button[aria-expanded]');
    await toggle.trigger("click"); await flushPromises();
    expect(toggle.attributes("aria-expanded")).toBe("true");
    expect(api).toHaveBeenCalledTimes(1);
    await toggle.trigger("click");
    expect(toggle.attributes('aria-expanded')).toBe('false');
    expect(wrapper.findAll('p').find(item => item.text() === 'tasks.empty')?.isVisible()).toBe(false);
    await toggle.trigger("click");
    expect(api).toHaveBeenCalledTimes(1);
    await wrapper.setProps({ userId: "two" }); await flushPromises();
    expect(api.mock.calls[1][1]).toContain("/two/");
    const signal = api.mock.calls[1][3] as AbortSignal;
    wrapper.unmount(); expect(signal.aborted).toBe(true);
  });
});
