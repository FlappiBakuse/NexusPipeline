import { defineComponent } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import HistoryDetailModal from "./HistoryDetailModal.vue";
import HistoryImagePreview from "./HistoryImagePreview.vue";
import { api, apiBlob } from "../../../platform/api";
import type { HistoryRecord } from "../utils/historyTypes";

vi.mock("../../../platform/api", () => ({ api: vi.fn(), apiBlob: vi.fn(), isAbortError: () => false }));
vi.mock("../../../platform/i18n", () => ({ t: (key: string) => key, getLocale: () => "en-US" }));
vi.mock("@bridge/index", () => ({ renderPluginSlot: vi.fn(async () => {}), disposePluginSlot: vi.fn(async () => {}) }));
const Modal = defineComponent({ props: ["open"], template: '<div v-if="open"><slot /><slot name="footer" /></div>' });
const create = vi.fn(() => "blob:owned-image"), revoke = vi.fn();
function record(id = "run"): HistoryRecord { return { id, scriptName: id, attemptDetails: [{ number: 1, screenshots: [{ id: "shot" }] }] }; }
function pending() {
  let resolve!: (blob: Blob) => void;
  const promise = new Promise<Blob>(done => { resolve = done; });
  return { promise, finish: () => resolve(new Blob(["image"], { type: "image/png" })) };
}
function open(value = record()) { return mount(HistoryDetailModal, { props: { record: value }, global: { stubs: {
  NxpModal: Modal, NxpScrollArea: { template: "<div><slot /></div>" }, HistoryImagePreview: true, TaskReportPanel: true,
} } }); }
beforeEach(() => {
  vi.mocked(api).mockReset(); vi.mocked(apiBlob).mockReset(); create.mockClear(); revoke.mockClear();
  vi.mocked(api).mockImplementation(async (_method, path) => ({ record: record(new URL(path, "http://fixture").searchParams.get("id")!) }));
  vi.stubGlobal("URL", class extends URL { static createObjectURL = create; static revokeObjectURL = revoke; });
});
afterEach(() => { vi.unstubAllGlobals(); });
describe("history screenshot lifecycle", () => {
  it("shares a pending thumbnail request with preview and revokes its single object URL", async () => {
    const image = pending(); vi.mocked(apiBlob).mockReturnValue(image.promise);
    const wrapper = open();
    try {
      await flushPromises(); await wrapper.get('button[aria-label="history.screenshot.item"]').trigger("click");
      expect(apiBlob).toHaveBeenCalledOnce(); image.finish(); await flushPromises();
      expect(create).toHaveBeenCalledOnce(); expect(wrapper.findComponent(HistoryImagePreview).props("image").url).toBe("blob:owned-image");
    } finally { wrapper.unmount(); }
    expect(revoke).toHaveBeenCalledExactlyOnceWith("blob:owned-image");
  });
  it("ignores a pending image and preview after the detail is cleared by its owner", async () => {
    const image = pending(); vi.mocked(apiBlob).mockReturnValue(image.promise);
    const wrapper = open();
    try {
      await flushPromises(); await wrapper.get('button[aria-label="history.screenshot.item"]').trigger("click");
      await wrapper.setProps({ record: null }); image.finish(); await flushPromises();
      expect(create).not.toHaveBeenCalled(); expect(wrapper.findComponent(HistoryImagePreview).exists()).toBe(false);
    } finally { wrapper.unmount(); }
  });
  it("does not reuse or publish a pending image belonging to the previous record", async () => {
    const old = pending(), current = pending(); vi.mocked(apiBlob).mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise);
    const wrapper = open();
    try {
      await flushPromises(); await wrapper.setProps({ record: record("replacement") }); await flushPromises();
      old.finish(); await flushPromises(); expect(create).not.toHaveBeenCalled();
      current.finish(); await flushPromises(); expect(create).toHaveBeenCalledOnce();
      expect(vi.mocked(apiBlob).mock.calls[1][0]).toContain("id=replacement");
      expect(wrapper.findComponent(HistoryImagePreview).exists()).toBe(false);
    } finally { wrapper.unmount(); }
    expect(revoke).toHaveBeenCalledExactlyOnceWith("blob:owned-image");
  });
});
