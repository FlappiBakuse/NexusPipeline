import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { EventStreamOptions } from "../../../platform/events";
import LiveTaskReports from "./LiveTaskReports.vue";
const mocks = vi.hoisted(() => ({ api: vi.fn(), stream: vi.fn(), close: vi.fn() }));
vi.mock("../../../platform/api", () => ({ api: mocks.api, isAbortError: () => false }));
vi.mock("../../../platform/events", () => ({ openEventStream: mocks.stream }));
vi.mock("../../../platform/i18n", () => ({ t: (key: string) => key }));
const snapshot = (revision: number, name: string) => ({ runId: "execution", revision, reports: [{ runId: name, scriptInstanceId: 'script', lifecycleOutcome: 'running' }] });
const mountPanel = () => mount(LiveTaskReports, {
  props: { executionId: "execution", currentScriptId: 'script' },
  global: { stubs: { TaskReportPanel: { props: ["report"], template: "<div>{{ report.runId }}</div>" } } },
});
describe("live task snapshots", () => {
  let events: EventStreamOptions;
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.stream.mockImplementation((options: EventStreamOptions) => { events = options; return { close: mocks.close, done: Promise.resolve() }; });
  });
  it("only shows the running report for the current instance and clears it when switching", async () => {
    const data = snapshot(1, 'current');
    data.reports.unshift({ runId: 'finished', scriptInstanceId: 'script', lifecycleOutcome: 'completed' });
    data.reports.push({ runId: 'other', scriptInstanceId: 'other-script', lifecycleOutcome: 'running' });
    mocks.api.mockResolvedValue(data);
    const wrapper = mountPanel(); await flushPromises();
    expect(wrapper.text()).toBe('current');
    await wrapper.setProps({ currentScriptId: 'other-script' }); await flushPromises();
    expect(wrapper.text()).toBe('other');
    await wrapper.setProps({ currentScriptId: '' }); await flushPromises();
    expect(wrapper.text()).toBe('');
    wrapper.unmount();
  });
  it("loads a snapshot, ignores stale events and replaces it after a reconnect gap", async () => {
    mocks.api.mockResolvedValueOnce(snapshot(4, "first"));
    const wrapper = mountPanel();
    await flushPromises();
    expect(wrapper.text()).toContain("first");
    await events.onEvent?.({ type: "task-report-changed", data: { runId: "execution", revision: 3 } });
    expect(mocks.api).toHaveBeenCalledTimes(1);
    mocks.api.mockResolvedValueOnce(snapshot(9, "reconnected"));
    await events.onMissed?.({ type: "missed" });
    await flushPromises();
    expect(wrapper.text()).toContain("reconnected");
    expect(wrapper.text()).not.toContain("first");
    wrapper.unmount();
    expect(mocks.close).toHaveBeenCalledOnce();
  });
  it("refetches when a newer event arrives during a snapshot request and aborts on disposal", async () => {
    let resolve!: (value: unknown) => void;
    mocks.api.mockImplementationOnce(() => new Promise(r => { resolve = r; })).mockResolvedValueOnce(snapshot(8, "latest"));
    const wrapper = mountPanel();
    await events.onEvent?.({ type: "task-report-changed", data: { runId: "execution", revision: 8 } });
    resolve(snapshot(2, "old"));
    await flushPromises();
    expect(wrapper.text()).toContain("latest");
    expect(mocks.api).toHaveBeenCalledTimes(2);
    wrapper.unmount();
    expect((mocks.api.mock.calls[1][3] as AbortSignal).aborted).toBe(true);
  });
});
