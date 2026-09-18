import { flushPromises, mount } from "@vue/test-utils";
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

vi.mock("../../platform/toast", () => ({
  toast: (...args: unknown[]) => toast(...args),
  clearFieldError: (id: string) => {
    const element = document.getElementById(id);
    element?.classList.remove("field-error");
    element?.removeAttribute("aria-invalid");
  },
  setRequiredFieldError: (id: string, focus = true) => {
    const element = document.getElementById(id);
    element?.classList.add("field-error");
    element?.setAttribute("aria-invalid", "true");
    if (focus) element?.focus();
  },
}));
vi.mock("../../platform/shell", () => ({
  setTopbarTitle: vi.fn(),
}));
vi.mock("@bridge/index", () => ({
  renderPluginSlot: vi.fn(),
  disposePluginSlot: vi.fn(),
}));

import ScriptEditorModal from "./ScriptEditorModal.vue";
import { buildScriptExport } from "./utils/scriptTransfer";
import { emptyScriptDraft } from "./utils/scriptTypes";

const plugin = { name: "hoyolab", displayName: "HoYoLab", kind: "data-specialized", configuredEnabled: true, runtimeEnabled: true };

function mountEditor(
  script: { id: string; name: string; pluginType?: string } | null,
  pluginName: string,
  pluginOverrides: Record<string, unknown> = {},
  existingNames: string[] = [],
) {
  return mount(ScriptEditorModal, {
    props: { script, plugin: pluginName, plugins: [{ ...plugin, ...pluginOverrides }], existingNames },
    global: { stubs: { Teleport: true } },
    attachTo: document.body,
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
    toast.mockClear();
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
    await wrapper.find(".nxp-modal-close").trigger("click");
    rejectPending(new Error("closed"));
    await nextTick();
    await nextTick();
    expect(toast).not.toHaveBeenCalledWith(expect.stringContaining("config_derive_failed"), "error");
    wrapper.unmount();
  });

  it("exposes one close control that shares the cancel handler", async () => {
    const wrapper = mountEditor(null, "hoyolab");

    const closeControls = wrapper.findAll(".nxp-modal-close");
    expect(closeControls).toHaveLength(1);
    expect(closeControls[0].attributes("aria-label")).toBe("Close");

    await closeControls[0].trigger("click");
    expect(wrapper.emitted("close")).toHaveLength(1);
    wrapper.unmount();

    const cancelWrapper = mountEditor(null, "hoyolab");
    const cancel = cancelWrapper.findAll(".nxp-modal-footer button").find(button => button.text() === "common.cancel");
    expect(cancel).toBeTruthy();
    await cancel!.trigger("click");
    expect(cancelWrapper.emitted("close")).toHaveLength(1);
    cancelWrapper.unmount();
  });

  it("marks every missing required field and clears its error after editing", async () => {
    const wrapper = mountEditor(null, "hoyolab");
    const save = wrapper.find(".nxp-modal-footer .primary");
    expect(save).toBeTruthy();
    await save!.trigger("click");

    expect(wrapper.find("#sm-name").classes()).toContain("field-error");
    expect(wrapper.find("#sm-root").classes()).toContain("field-error");
    expect(wrapper.find("#sm-game-exe").classes()).toContain("field-error");
    expect(wrapper.find("#sm-name").attributes("aria-invalid")).toBe("true");

    await wrapper.find("#sm-name").setValue("Alice");
    expect(wrapper.find("#sm-name").classes()).not.toContain("field-error");
    wrapper.unmount();
  });

  it("restores self-managed PC launch controls and keeps the dormant values visible", async () => {
    const wrapper = mountEditor(null, "hoyolab", { selfManagedPcLaunch: true });

    expect(wrapper.find("#sm-launch").attributes("disabled")).toBeDefined();
    expect(wrapper.find("#sm-game-args").attributes("disabled")).toBeDefined();
    expect(wrapper.find("#sm-game-wait").attributes("disabled")).toBeDefined();
    expect(wrapper.find("#sm-self-managed-hint").exists()).toBe(true);
    wrapper.unmount();
  });

  it("hides the self-managed launch hint for BAAH while retaining the disabled controls", async () => {
    const wrapper = mountEditor(null, "baah", {
      name: "baah",
      displayName: "蔚蓝档案爱丽丝助手",
      selfManagedPcLaunch: true,
    });

    expect(wrapper.find("#sm-launch").attributes("disabled")).toBeDefined();
    expect(wrapper.find("#sm-game-args").attributes("disabled")).toBeDefined();
    expect(wrapper.find("#sm-game-wait").attributes("disabled")).toBeDefined();
    expect(wrapper.find("#sm-self-managed-hint").exists()).toBe(false);
    wrapper.unmount();
  });

  it("shows import and export only for the matching general-script modes", () => {
    const edit = mountEditor({ id: "script-1", name: "Existing" }, "");
    expect(edit.findAll(".script-transfer-actions button").map(button => button.text())).toEqual(["导出"]);
    edit.unmount();

    const create = mountEditor(null, "");
    expect(create.findAll(".script-transfer-actions button").map(button => button.text())).toEqual(["导入"]);
    create.unmount();

    const specialized = mountEditor({ id: "script-2", name: "Special", pluginType: "hoyolab" }, "hoyolab");
    expect(specialized.findAll(".script-transfer-actions button")).toHaveLength(0);
    specialized.unmount();
  });

  it("imports a valid file without mutating until validation succeeds and derives relative paths after root input", async () => {
    const wrapper = mountEditor(null, "", {}, ["Daily"]);
    const file = new File([JSON.stringify(buildScriptExport({
      ...emptyScriptDraft,
      name: "Daily",
      rootPath: "C:\\Source",
      mainExe: "C:\\Source\\bin\\tool.exe",
      configPath: "C:\\Source\\config",
      logPath: "C:\\Source\\logs\\run.log",
      gameExe: "C:\\Games\\game.exe",
    }))], "daily.nxpscript.json", { type: "application/json" });
    const input = wrapper.get("input[type='file']").element as HTMLInputElement;
    Object.defineProperty(input, "files", { configurable: true, value: [file] });
    await wrapper.get("input[type='file']").trigger("change");
    await flushPromises();

    expect((wrapper.get("#sm-name").element as HTMLInputElement).value).toBe("Daily-2");
    const pickers = wrapper.findAllComponents({ name: "NxpPathPicker" });
    const rootPicker = pickers.find(item => item.props("id") === "sm-root")!;
    const mainPicker = pickers.find(item => item.props("id") === "sm-exe")!;
    expect(rootPicker.props("modelValue")).toBe("");
    expect(mainPicker.props("modelValue")).toBe("");

    await rootPicker.vm.$emit("update:modelValue", "D:/Imported");
    await nextTick();
    expect(mainPicker.props("modelValue")).toBe("D:\\Imported\\bin\\tool.exe");
    expect(pickers.find(item => item.props("id") === "sm-config")?.props("modelValue")).toBe("D:\\Imported\\config");
    wrapper.unmount();
  });

  it("keeps the draft unchanged for an invalid import and downloads an edited general script", async () => {
    const createWrapper = mountEditor(null, "");
    const input = createWrapper.get("input[type='file']").element as HTMLInputElement;
    Object.defineProperty(input, "files", {
      configurable: true,
      value: [new File(["{"], "broken.nxpscript.json", { type: "application/json" })],
    });
    await createWrapper.get("input[type='file']").trigger("change");
    await flushPromises();
    expect((createWrapper.get("#sm-name").element as HTMLInputElement).value).toBe("");
    expect(toast).toHaveBeenCalledWith("导入文件不是有效 JSON", "error");
    createWrapper.unmount();

    const createObjectUrl = vi.fn().mockReturnValue("blob:nexus-script-test");
    const revokeObjectUrl = vi.fn();
    Object.defineProperty(URL, "createObjectURL", { configurable: true, writable: true, value: createObjectUrl });
    Object.defineProperty(URL, "revokeObjectURL", { configurable: true, writable: true, value: revokeObjectUrl });
    let download = "";
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      download = this.download;
    });
    const editWrapper = mountEditor({ id: "script-1", name: "A:B" }, "");
    await editWrapper.find(".script-transfer-actions button").trigger("click");
    expect(createObjectUrl).toHaveBeenCalledTimes(1);
    expect(download).toBe("A_B.nxpscript.json");
    expect(revokeObjectUrl).toHaveBeenCalledWith("blob:nexus-script-test");
    click.mockRestore();
    editWrapper.unmount();
  });
});
