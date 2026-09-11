import { mount } from "@vue/test-utils";
import { nextTick } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";

/** 组件级验证 ScriptEditorModal 的两条用户可到达的 root 修改路径
 *  （手工输入与原生目录选择）都经过生产 probe controller。 */

const probeScriptRoot = vi.fn().mockResolvedValue({ ok: true });
const browseNativeDialog = vi.fn();
const createScript = vi.fn().mockResolvedValue(undefined);
const updateScript = vi.fn().mockResolvedValue(undefined);
const toast = vi.fn();

vi.mock("./services/scriptsApi", () => ({
  probeScriptRoot: (input: unknown) => probeScriptRoot(input),
  browseNativeDialog: (input: unknown) => browseNativeDialog(input),
  createScript: (input: unknown) => createScript(input),
  updateScript: (id: string, input: unknown) => updateScript(id, input),
}));

vi.mock("@legacy/core/ui.js", () => ({
  toast: (...args: unknown[]) => toast(...args),
  setTopbarTitle: vi.fn(),
}));

vi.mock("@legacy/core/plugin-slots.js", () => ({ renderPluginSlot: vi.fn() }));
vi.mock("@legacy/core/plugin-runtime.js", () => ({ disposePluginSlot: vi.fn() }));

import ScriptEditorModal from "./ScriptEditorModal.vue";

const plugin = { name: "hoyolab", displayName: "HoYoLab", kind: "data-specialized", configuredEnabled: true, runtimeEnabled: true };

function mountEditor(script: { id: string; name: string; pluginType?: string } | null, pluginName: string) {
  return mount(ScriptEditorModal, {
    props: { script, plugin: pluginName, plugins: [plugin] },
    global: { stubs: { Teleport: true } },
  });
}

async function setRootPath(wrapper: ReturnType<typeof mountEditor>, value: string) {
  const picker = wrapper.findComponent({ name: "NxpPathPicker" });
  await picker.vm.$emit("update:modelValue", value);
  await picker.vm.$emit("change", value);
  await nextTick();
}

describe("ScriptEditorModal root probe", () => {
  beforeEach(() => {
    probeScriptRoot.mockClear();
    browseNativeDialog.mockReset();
  });

  it("probes once when a specialized script root changes manually", async () => {
    const wrapper = mountEditor(null, "hoyolab");
    await setRootPath(wrapper, "D:/Game");
    expect(probeScriptRoot).toHaveBeenCalledTimes(1);
    expect(probeScriptRoot.mock.calls[0][0]).toEqual({ pluginType: "hoyolab", rootPath: "D:/Game", inputs: {} });
    wrapper.unmount();
  });

  it("does not probe a general script", async () => {
    const wrapper = mountEditor(null, "");
    await setRootPath(wrapper, "D:/Game");
    expect(probeScriptRoot).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it("does not probe an empty root", async () => {
    const wrapper = mountEditor(null, "hoyolab");
    await setRootPath(wrapper, "   ");
    expect(probeScriptRoot).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it("probes when the root is chosen through the native folder picker", async () => {
    browseNativeDialog.mockResolvedValue({ path: "D:/Native" });
    const wrapper = mountEditor(null, "hoyolab");
    const picker = wrapper.findComponent({ name: "NxpPathPicker" });
    await picker.vm.$emit("browse", "folder");
    await nextTick();
    await nextTick();
    expect(browseNativeDialog).toHaveBeenCalledTimes(1);
    expect(probeScriptRoot).toHaveBeenCalledTimes(1);
    expect(probeScriptRoot.mock.calls[0][0]).toEqual({ pluginType: "hoyolab", rootPath: "D:/Native", inputs: {} });
    wrapper.unmount();
  });

  it("does not repeat the probe for the same committed root", async () => {
    const wrapper = mountEditor(null, "hoyolab");
    await setRootPath(wrapper, "D:/Game");
    await setRootPath(wrapper, "D:/Game");
    expect(probeScriptRoot).toHaveBeenCalledTimes(1);
    wrapper.unmount();
  });

  it("suppresses an in-flight failure after the editor closes", async () => {
    let rejectPending: (reason: unknown) => void = () => {};
    probeScriptRoot.mockImplementationOnce(() => new Promise((_resolve, reject) => { rejectPending = reject; }));
    const wrapper = mountEditor(null, "hoyolab");
    await setRootPath(wrapper, "D:/Game");
    await wrapper.find(".modal-close").trigger("click");
    rejectPending(new Error("closed"));
    await nextTick();
    await nextTick();
    expect(toast).not.toHaveBeenCalledWith(expect.stringContaining("config_derive_failed"), "error");
    wrapper.unmount();
  });
});
