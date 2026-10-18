import { defineComponent } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useOperationAccess } from "./operation-access";
import type { ClientCapabilities } from "./client-capabilities";

const fixture = vi.hoisted(() => ({ get: vi.fn(), changed: undefined as undefined | ((paused: boolean) => void), unsubscribe: vi.fn() }));
vi.mock("./client-capabilities", () => ({ getCapabilities: fixture.get }));
vi.mock("./service-traffic", () => ({ onServiceTrafficChanged: (callback: (paused: boolean) => void) => { fixture.changed = callback; return fixture.unsubscribe; } }));
vi.mock("./i18n", () => ({ t: (key: string) => key }));
const Probe = defineComponent({ setup: () => useOperationAccess("hostFilePicker"), template: "<output>{{ allowed }}</output>" });
const local: ClientCapabilities = { schemaVersion: 1, connectionKind: "local", operations: {
  general: { allowed: true, denyReason: null }, hostFilePicker: { allowed: true, denyReason: null }, nativeConfigEditor: { allowed: true, denyReason: null },
} };
function pending() {
  let resolve!: (value: ClientCapabilities) => void, reject!: (reason: Error) => void;
  const promise = new Promise<ClientCapabilities>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
beforeEach(() => { fixture.get.mockReset(); fixture.unsubscribe.mockReset(); });
afterEach(() => { fixture.changed = undefined; });
describe("operation access lifecycle", () => {
  it("keeps the current authorization when an obsolete request fails after reconnect", async () => {
    const old = pending(), current = pending();
    fixture.get.mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise);
    const wrapper = mount(Probe);
    try {
      fixture.changed!(true); fixture.changed!(false);
      current.resolve(local); await flushPromises(); expect(wrapper.text()).toBe("true");
      old.reject(new DOMException("Connection changed", "AbortError")); await flushPromises();
      expect(wrapper.text()).toBe("true");
    } finally { wrapper.unmount(); }
    expect(fixture.unsubscribe).toHaveBeenCalledOnce();
  });
  it("does not restore authorization from a request completed while service traffic is paused", async () => {
    const old = pending(); fixture.get.mockReturnValue(old.promise);
    const wrapper = mount(Probe);
    try {
      fixture.changed!(true); old.resolve(local); await flushPromises();
      expect(wrapper.text()).toBe("false");
    } finally { wrapper.unmount(); }
    expect((fixture.get.mock.calls[0][0] as AbortSignal).aborted).toBe(true);
  });
});
