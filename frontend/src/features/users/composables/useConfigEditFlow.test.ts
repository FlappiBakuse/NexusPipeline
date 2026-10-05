import { describe, expect, it, vi } from "vitest";
import { useConfigEditFlow, type ConfigEditFlowAdapters } from "./useConfigEditFlow";

describe("configuration edit lifecycle", () => {
  it("selects native candidates from the actual HTTP error envelope and refreshes after save", async () => {
    const changed = vi.fn();
    const start = vi.fn().mockRejectedValueOnce({ code: "config_input_mismatch", data: {
      ok: false, code: "config_input_mismatch", args: { inputName: "instance", candidates: ["default"] },
    } }).mockResolvedValueOnce({});
    const adapters: ConfigEditFlowAdapters = {
      getStatus: async () => ({ hasSnapshot: false }), start, finish: async () => ({}),
      listSessions: async () => [], notify: vi.fn(), translate: key => key, onTransactionChanged: changed,
    };
    const flow = useConfigEditFlow(adapters);
    await flow.open({ userId: "account", scriptId: "script", userName: "Account", scriptName: "Script" }, false);
    await flow.chooseMode("reuse");
    expect(flow.configCandidates.value).toBeNull();
    expect(changed).not.toHaveBeenCalled();
    expect(start).toHaveBeenNthCalledWith(1, "account", "script", { action: "start", mode: "reuse" });
    expect(start).toHaveBeenLastCalledWith("account", "script", {
      action: "start", mode: "reuse", configInputName: "instance", configInputValue: "default",
    });
    await flow.finish("done");
    expect(changed).toHaveBeenCalledExactlyOnceWith("account");
    expect(flow.configEdit.value).toBeNull();
    start.mockRejectedValueOnce({ code: "config_input_mismatch", data: {
      args: { inputName: "instance", candidates: ["first", "second"] },
    } }).mockResolvedValueOnce({});
    await flow.open({ userId: "other", scriptId: "script", userName: "Other", scriptName: "Script" }, false);
    await flow.chooseMode("reuse");
    expect(flow.configCandidates.value?.candidates).toEqual(["first", "second"]);
    expect(start).toHaveBeenCalledTimes(3);
    await flow.chooseCandidate("second");
    expect(start).toHaveBeenLastCalledWith("other", "script", {
      action: "start", mode: "reuse", configInputName: "instance", configInputValue: "second",
    });
    await flow.finish("cancel");
    start.mockRejectedValue({ code: "config_input_mismatch", data: {
      args: { inputName: "instance", candidates: ["changed"] },
    } });
    await flow.open({ userId: "third", scriptId: "script", userName: "Third", scriptName: "Script" }, false);
    await flow.chooseMode("reuse");
    expect(start).toHaveBeenCalledTimes(6);
    expect(flow.configCandidates.value?.candidates).toEqual(["changed"]);
    expect(flow.configEdit.value).toBeNull();

    const sessions = [{ userId: "account", scriptId: "script", editMode: "reuse" }];
    const finish = vi.fn().mockRejectedValueOnce(new Error("save rejected")).mockResolvedValue({});
    const restored = useConfigEditFlow({ ...adapters, listSessions: async () => sessions, finish });
    const users = [{ id: "account", name: "Account", bindings: [{ scriptInstanceId: "script" }] }];
    const scripts = [{ id: "script", name: "Script" }];
    start.mockClear();
    await restored.restoreExisting(users, scripts);
    expect(restored.configEdit.value).toEqual({ userId: "account", scriptId: "script",
      userName: "Account", scriptName: "Script", mode: "reuse" });
    expect(restored.isOpen.value).toBe(true);
    expect(start).not.toHaveBeenCalled();
    await restored.finish("done");
    expect(restored.configEdit.value?.userId).toBe("account");
    await restored.finish("done");
    expect(finish).toHaveBeenLastCalledWith("account", "script", "done");
    expect(restored.isOpen.value).toBe(false);
    await restored.restoreExisting(users, scripts, () => false);
    expect(restored.isOpen.value).toBe(false);
    await restored.restoreExisting(users, scripts);
    await restored.finish("cancel");
    expect(finish).toHaveBeenLastCalledWith("account", "script", "cancel");
    expect(restored.isOpen.value).toBe(false);
  });
});
