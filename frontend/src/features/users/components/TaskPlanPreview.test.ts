import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import TaskPlanPreview from "./TaskPlanPreview.vue";
const { api } = vi.hoisted(() => ({ api: vi.fn() }));
vi.mock("../../../platform/api", () => ({ api }));
vi.mock("../../../platform/i18n", () => ({ t: (key: string) => key }));
describe("task plan disclosure", () => {
  it("discards an in-flight assessment when the saved binding revision changes", async () => {
    api.mockReset();
    let resolveOld!: (value: unknown) => void;
    api.mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve; }));
    api.mockResolvedValue({ plan: { tasks: [], coverage: "partial", diagnostics: [],
      configAssessment: { schemaVersion: '1', checks: [] } } });
    const wrapper = mount(TaskPlanPreview, { props: { userId: "one", scriptId: "script", revision: 0 } });
    await wrapper.get('button[aria-expanded]').trigger('click');
    const signal = api.mock.calls[0][3] as AbortSignal;
    await wrapper.setProps({ revision: 1 }); await flushPromises();
    expect(signal.aborted).toBe(true);
    expect(api).toHaveBeenCalledTimes(2);
    resolveOld({ error: 'old_failure' }); await flushPromises();
    expect(wrapper.text()).not.toContain('old_failure');
    await wrapper.get('button[aria-expanded]').trigger('click');
    await wrapper.setProps({ revision: 2 });
    expect(api).toHaveBeenCalledTimes(2);
    await wrapper.get('button[aria-expanded]').trigger('click'); await flushPromises();
    expect(api).toHaveBeenCalledTimes(3);
    wrapper.unmount(); api.mockReset();
  });
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
