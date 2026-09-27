import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import TaskPlanPreview from "./TaskPlanPreview.vue";
const { api } = vi.hoisted(() => ({ api: vi.fn() }));
vi.mock("../../../platform/api", () => ({ api }));
vi.mock("../../../platform/i18n", () => ({ t: (key: string) => key }));
vi.mock("../../../platform/toast", () => ({ toast: vi.fn() }));
describe("task plan disclosure", () => {
  it("previews the bound field without applying, applies the exact token and then reassesses", async () => {
    api.mockReset();
    api.mockResolvedValue({ plan: { tasks: [], coverage: "complete", diagnostics: [] } });
    const wrapper = mount(TaskPlanPreview, { props: { userId: "one", scriptId: "script" } });
    await wrapper.get('button[aria-expanded]').trigger('click'); await flushPromises();
    api.mockResolvedValueOnce({ plugin: "march7th", userId: "one", field: "after_finish",
      oldValue: "Shutdown", proposedValue: "None", impact: "only this field", token: "bound-token" });
    const button = (label: string) => wrapper.findAll('button').find(item => item.text() === label)!;
    await button('tasks.repair_preview').trigger('click'); await flushPromises();
    expect(api.mock.calls[1][0]).toBe('GET');
    expect(api.mock.calls[1][1]).toContain('/one/');
    expect(wrapper.get('[aria-label="tasks.repair_preview"]').text()).toContain('Shutdown → None');
    expect(api.mock.calls.some(call => call[0] === 'POST')).toBe(false);
    await button('tasks.repair_apply').trigger('click'); await flushPromises();
    expect(api.mock.calls[2]).toEqual(['POST', api.mock.calls[1][1], { token: 'bound-token' }]);
    expect(api.mock.calls[3][0]).toBe('GET');
    expect(api.mock.calls[3][1]).toContain('/one/');
    expect(wrapper.find('[aria-label="tasks.repair_preview"]').exists()).toBe(false);
    wrapper.unmount(); api.mockReset();
  });
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
