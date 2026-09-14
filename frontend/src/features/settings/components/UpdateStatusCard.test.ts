import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";

const apiMock = vi.fn();
let response: Record<string, unknown> = {};

vi.mock("../../../platform/api", () => ({
  api: (...args: unknown[]) => apiMock(...args),
  isAbortError: () => false,
}));
vi.mock("../../../platform/toast", () => ({ toast: vi.fn() }));

import UpdateStatusCard from "./UpdateStatusCard.vue";

describe("UpdateStatusCard actions", () => {
  beforeEach(() => {
    response = { current: "0.16.0", channel: "stable", state: "idle", checked: true, available: false };
    apiMock.mockReset();
    apiMock.mockImplementation(async (method: string) => method === "GET" ? response : response);
  });

  it("hides check-for-updates after a ready package is downloaded", async () => {
    response = { current: "0.16.0", channel: "stable", state: "ready", latest: "0.16.1", available: true };
    const wrapper = mount(UpdateStatusCard);
    await flushPromises();

    expect(wrapper.find("button").text()).not.toContain("common.check_for_updates");
    expect(wrapper.findAll("button").some(button => button.text() === "common.update_now")).toBe(true);
    expect(wrapper.findAll("button").some(button => button.text() === "common.update_on_next_startup")).toBe(true);
    wrapper.unmount();
  });

  it("keeps checking available when idle and replaces it with cancel while downloading", async () => {
    let wrapper = mount(UpdateStatusCard);
    await flushPromises();
    expect(wrapper.findAll("button").some(button => button.text() === "common.check_for_updates")).toBe(true);
    wrapper.unmount();

    response = { current: "0.16.0", channel: "stable", state: "downloading", latest: "0.16.1", progress: 40 };
    wrapper = mount(UpdateStatusCard);
    await flushPromises();
    expect(wrapper.findAll("button").some(button => button.text() === "common.check_for_updates")).toBe(false);
    expect(wrapper.findAll("button").some(button => button.text() === "common.cancel_download")).toBe(true);
    wrapper.unmount();
  });
});
