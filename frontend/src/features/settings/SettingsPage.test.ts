import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import SettingsPage from "./SettingsPage.vue";
import SettingsRemoteAccessSection from "./components/SettingsRemoteAccessSection.vue";
import SettingsServiceSection from "./components/SettingsServiceSection.vue";
import ServiceRestartNotice from "./components/ServiceRestartNotice.vue";
import NxpSelect from "../../ui/primitives/NxpSelect.vue";
import NxpConfirmDialog from "../../ui/composites/NxpConfirmDialog.vue";

const fixture = vi.hoisted(() => ({ api: vi.fn(), restart: vi.fn(), clipboard: vi.fn(), preferences: vi.fn(), toast: vi.fn(), dirty: null as null | (() => boolean) }));
vi.mock("../../platform/api", () => ({ api: fixture.api, isAbortError: () => false }));
vi.mock("../../platform/toast", () => ({ toast: fixture.toast }));
vi.mock("../../platform/desktop", () => ({ desktopBridge: () => ({ writeClipboardText: fixture.clipboard, setClientPreferences: fixture.preferences }) }));
vi.mock("../../platform/service-recovery", () => ({
  registerRecoveryDirtyGuard: (guard: () => boolean) => { fixture.dirty = guard; return () => { fixture.dirty = null; }; },
  beginServiceRecovery: fixture.restart, resumeServiceRecovery: vi.fn(),
}));
vi.mock("@bridge/index", () => ({ renderPluginSlot: vi.fn(), disposePluginSlot: vi.fn() }));
let wrapper: VueWrapper;
let stored: Record<string, unknown>;
let secret: string;

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  stored = { allowRemoteAccess: false, webPort: 58731, mcpPort: 58732, historyRetentionDays: 7 };
  secret = "";
  fixture.api.mockImplementation(async (method: string, url: string, payload?: Record<string, unknown>) => {
    if (url === "/api/status") return { lightweightMode: false };
    if (method === "PUT") {
      if (payload?.secretKey === "accessToken") secret = String(payload.secretValue);
      else Object.assign(stored, payload);
    }
    return { settings: { ...stored, accessToken: secret ? "enc:***" : "" }, status: { remote: { internalAddress: null, publicAddress: null, port: 58731 } } };
  });
});
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = ""; vi.unstubAllGlobals(); });

async function setup() {
  wrapper = mount(SettingsPage, { attachTo: document.body, global: {
    plugins: [createPinia()], stubs: { SettingsNotificationsSection: true, SettingsNetworkSection: true, SettingsUpdatesSection: true, DiagnosticsSection: true },
  } });
  await flushPromises();
  wrapper.getComponent(SettingsRemoteAccessSection).vm.$emit("toggle");
  await flushPromises();
}

describe("remote settings persistence", () => {
  it("saves a typed token without blur and copies through the desktop bridge", async () => {
    await setup();
    (wrapper.get("#st-token").element as HTMLInputElement).value = "owned-token";
    await wrapper.get("#st-token").trigger("input");
    await flushPromises();
    expect(secret).toBe("owned-token");
    expect(fixture.api).toHaveBeenCalledWith("PUT", "/api/settings", { secretKey: "accessToken", secretValue: "owned-token" });
    expect(fixture.dirty?.()).toBe(false);
    await wrapper.getComponent(SettingsRemoteAccessSection).props("copyToken")();
    expect(fixture.clipboard).toHaveBeenCalledWith("owned-token");
    expect(fixture.toast).toHaveBeenLastCalledWith("settings.access_token_copied");
  });

  it("waits for the latest remote save before requesting restart", async () => {
    await setup();
    let release!: () => void;
    const response = fixture.api.getMockImplementation()!;
    fixture.api.mockImplementation(async (...args: unknown[]) => {
      if (args[0] === "PUT") await new Promise<void>(resolve => { release = resolve; });
      return response(...args);
    });
    await wrapper.get("#st-remote").trigger("click");
    await flushPromises();
    const notice = wrapper.getComponent(ServiceRestartNotice);
    notice.getComponent(NxpConfirmDialog).vm.$emit("confirm");
    await flushPromises();
    expect(fixture.restart).not.toHaveBeenCalled();
    release();
    await flushPromises();
    expect(stored.allowRemoteAccess).toBe(true);
    expect(fixture.dirty?.()).toBe(false);
    expect(fixture.restart).toHaveBeenCalledTimes(1);
  });

  it("keeps newer remote edits when an older response arrives", async () => {
    await setup();
    let release!: () => void;
    const response = fixture.api.getMockImplementation()!;
    let first = true;
    fixture.api.mockImplementation(async (...args: unknown[]) => {
      if (args[0] === "PUT" && first) { first = false; await new Promise<void>(resolve => { release = resolve; }); }
      return response(...args);
    });
    await wrapper.get("#st-remote").trigger("click");
    await flushPromises();
    await wrapper.get("#st-remote").trigger("click");
    release();
    await flushPromises();
    expect(stored.allowRemoteAccess).toBe(false);
    expect(wrapper.get("#st-remote").attributes("aria-pressed")).toBe("false");
    expect(fixture.dirty?.()).toBe(false);
  });

  it("preserves the token draft and blocks restart after a failed save", async () => {
    await setup();
    fixture.api.mockRejectedValueOnce(new Error("durable-save-failed"));
    await wrapper.get("#st-token").setValue("unsaved-owned-token");
    await flushPromises();
    const notice = wrapper.getComponent(ServiceRestartNotice);
    notice.getComponent(NxpConfirmDialog).vm.$emit("confirm");
    await flushPromises();
    expect(secret).toBe("");
    expect(wrapper.get("#st-token").element).toHaveProperty("value", "unsaved-owned-token");
    expect(fixture.dirty?.()).toBe(true);
    expect(fixture.restart).not.toHaveBeenCalled();
  });

  it("keeps the saved token when the draft is cleared without leaving a dirty guard", async () => {
    await setup();
    await wrapper.get("#st-token").setValue("owned-token");
    await flushPromises();
    const writes = fixture.api.mock.calls.filter(args => args[0] === "PUT").length;
    await wrapper.get("#st-token").setValue("");
    await flushPromises();
    expect(secret).toBe("owned-token");
    expect(fixture.api.mock.calls.filter(args => args[0] === "PUT")).toHaveLength(writes);
    expect(fixture.dirty?.()).toBe(false);
    fixture.api.mockRejectedValueOnce(new Error("durable-save-failed"));
    await wrapper.get("#st-token").setValue("failed-token");
    await flushPromises();
    await wrapper.get("#st-token").setValue("");
    await wrapper.getComponent(ServiceRestartNotice).props("beforeRestart")!();
    expect(secret).toBe("owned-token");
    expect(fixture.dirty?.()).toBe(false);
    let reject!: (reason: Error) => void;
    fixture.api.mockImplementationOnce(() => new Promise((_resolve, fail) => { reject = fail; }));
    await wrapper.get("#st-token").setValue("pending-token");
    await flushPromises();
    await wrapper.get("#st-token").setValue("");
    reject(new Error("durable-save-failed"));
    await wrapper.getComponent(ServiceRestartNotice).props("beforeRestart")!();
    expect(secret).toBe("owned-token");
    expect(fixture.dirty?.()).toBe(false);
  });

  it("does not acknowledge or overwrite an unrelated unsaved field when a token saves", async () => {
    await setup();
    wrapper.getComponent(SettingsServiceSection).vm.$emit("toggle");
    await flushPromises();
    const retention = wrapper.get("#st-retention");
    (retention.element as HTMLInputElement).value = "9";
    await retention.trigger("input");
    wrapper.getComponent(SettingsRemoteAccessSection).vm.$emit("toggle");
    await flushPromises();
    await wrapper.get("#st-token").setValue("owned-token");
    await flushPromises();
    expect(secret).toBe("owned-token");
    expect(stored.historyRetentionDays).toBe(7);
    expect(wrapper.getComponent(SettingsServiceSection).props("settings").historyRetentionDays).toBe(9);
    expect(fixture.dirty?.()).toBe(true);
  });
});

describe("client language persistence", () => {
  it("stores the interface locale without changing Host settings", async () => {
    fixture.preferences.mockResolvedValue(undefined);
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, json: async () => ({}) })));
    await setup();
    const service = wrapper.getComponent(SettingsServiceSection);
    service.vm.$emit("toggle");
    await flushPromises();
    const language = service.findAllComponents(NxpSelect).find(select => select.props("id") === "settings-locale")!;
    language.vm.$emit("change", "en-US");
    await flushPromises();
    expect(localStorage.getItem("nexus-locale")).toBe("en-US");
    expect(fixture.preferences).toHaveBeenCalledWith({ locale: "en-US" });
    expect(fixture.api.mock.calls.filter(args => args[0] === "PUT")).toHaveLength(0);
    expect(fixture.dirty?.()).toBe(false);
    expect(fixture.restart).not.toHaveBeenCalled();
  });
});
