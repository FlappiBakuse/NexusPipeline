import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent } from "vue";

/** 全局重启提示：状态来源为 shell store，重启编排委托给平台 helper。 */

const restartServiceMock = vi.fn(async () => "ready" as "ready" | "timeout" | "failed");
let statusResponse: { lightweightMode?: boolean } = { lightweightMode: false };

vi.mock("../../../platform/service-restart", () => ({
  restartService: (...args: unknown[]) => restartServiceMock(...(args as [])),
}));
vi.mock("../../../platform/api", () => ({
  api: async () => statusResponse,
  isAbortError: () => false,
}));

import ServiceRestartNotice from "./ServiceRestartNotice.vue";
import { useShellStore } from "../../../stores/shell";

const Page = defineComponent({
  components: { ServiceRestartNotice },
  template: `<ServiceRestartNotice />`,
});

async function mountNotice() {
  const wrapper = mount(Page, { attachTo: document.body });
  await flushPromises();
  return wrapper;
}

describe("service restart notice", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    restartServiceMock.mockClear();
    restartServiceMock.mockResolvedValue("ready");
    statusResponse = { lightweightMode: false };
  });

  it("stays hidden while no restart is pending", async () => {
    const wrapper = await mountNotice();

    expect(wrapper.find("[data-testid='service-restart-notice']").exists()).toBe(false);
    wrapper.unmount();
  });

  it("offers the restart action as soon as a restart is required", async () => {
    const shell = useShellStore();
    const wrapper = await mountNotice();

    shell.markRestartRequired("plugin.install");
    await flushPromises();

    expect(wrapper.get("[data-testid='service-restart-notice']").text()).toContain("settings.service.restart_notice");
    expect(wrapper.get("[data-testid='restart-service']").text()).toBe("settings.restart_service");
    wrapper.unmount();
  });

  it("restarts through the platform helper after confirmation", async () => {
    vi.stubGlobal("confirm", vi.fn(() => true));
    const shell = useShellStore();
    const wrapper = await mountNotice();
    shell.markRestartRequired("plugin.install");
    await flushPromises();

    await wrapper.get("[data-testid='restart-service']").trigger("click");
    await flushPromises();

    expect(restartServiceMock).toHaveBeenCalledTimes(1);
    expect(shell.restartRequired).toBe(true);
    expect(shell.restartError).toBe("");
    vi.unstubAllGlobals();
    wrapper.unmount();
  });

  it("keeps the request pending when the user cancels the confirmation", async () => {
    vi.stubGlobal("confirm", vi.fn(() => false));
    const shell = useShellStore();
    const wrapper = await mountNotice();
    shell.markRestartRequired("plugin.install");
    await flushPromises();

    await wrapper.get("[data-testid='restart-service']").trigger("click");
    await flushPromises();

    expect(restartServiceMock).not.toHaveBeenCalled();
    expect(shell.restarting).toBe(false);
    vi.unstubAllGlobals();
    wrapper.unmount();
  });

  it("hides the restart action while the restart is in progress", async () => {
    vi.stubGlobal("confirm", vi.fn(() => true));
    let resolveRestart: (outcome: "ready" | "timeout" | "failed") => void = () => {};
    restartServiceMock.mockImplementation(() => new Promise(resolve => {
      resolveRestart = resolve;
    }));
    const shell = useShellStore();
    const wrapper = await mountNotice();
    shell.markRestartRequired("plugin.update");
    await flushPromises();

    await wrapper.get("[data-testid='restart-service']").trigger("click");
    await flushPromises();

    expect(shell.restarting).toBe(true);
    expect(wrapper.find("[data-testid='restart-service']").exists()).toBe(false);
    expect(wrapper.get("[data-testid='service-restart-notice']").text()).toContain("settings.service_restarting");

    resolveRestart("ready");
    await flushPromises();
    expect(shell.restarting).toBe(true);
    wrapper.unmount();
    vi.unstubAllGlobals();
  });

  it("offers the same restart action again after a timeout", async () => {
    vi.stubGlobal("confirm", vi.fn(() => true));
    restartServiceMock.mockResolvedValue("timeout");
    const shell = useShellStore();
    const wrapper = await mountNotice();
    shell.markRestartRequired("plugin.update");
    await flushPromises();

    await wrapper.get("[data-testid='restart-service']").trigger("click");
    await flushPromises();

    expect(shell.restartError).toBe("settings.service.restart_timeout");
    expect(wrapper.get("[data-testid='restart-service']").text()).toBe("settings.restart_service");
    vi.unstubAllGlobals();
    wrapper.unmount();
  });

  it("keeps the manual restart wording in lightweight mode", async () => {
    statusResponse = { lightweightMode: true };
    const shell = useShellStore();
    const wrapper = await mountNotice();
    shell.markRestartRequired("settings");
    await flushPromises();

    expect(wrapper.find("[data-testid='restart-service']").exists()).toBe(false);
    expect(wrapper.get("[data-testid='service-restart-notice']").text()).toContain("settings.service.lightweight_restart");
    wrapper.unmount();
  });
});
