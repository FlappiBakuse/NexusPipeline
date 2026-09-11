import { describe, expect, it, vi } from "vitest";
import { useConfigEditFlow } from "./useConfigEditFlow";
import type { ConfigEditFlowAdapters } from "./useConfigEditFlow";

function createAdapters(overrides: Partial<ConfigEditFlowAdapters> = {}): ConfigEditFlowAdapters & {
  start: ReturnType<typeof vi.fn>;
  finish: ReturnType<typeof vi.fn>;
  listSessions: ReturnType<typeof vi.fn>;
  notify: ReturnType<typeof vi.fn>;
  setDocumentTitle: ReturnType<typeof vi.fn>;
} {
  const adapters: ConfigEditFlowAdapters = {
    getStatus: vi.fn().mockResolvedValue({ hasSnapshot: true }),
    start: vi.fn().mockResolvedValue(undefined),
    finish: vi.fn().mockResolvedValue(null),
    listSessions: vi.fn().mockResolvedValue([]),
    createRequesterWindowToken: vi.fn(() => "token-1234"),
    waitForRequesterTitlePaint: vi.fn().mockResolvedValue(undefined),
    getDocumentTitle: () => "original",
    setDocumentTitle: vi.fn(),
    notify: vi.fn(),
    translate: (key: string, args?: Record<string, unknown>) => (args ? `${key}:${JSON.stringify(args)}` : key),
    onTransactionChanged: vi.fn(),
    ...overrides,
  };
  return adapters as ConfigEditFlowAdapters & {
    start: ReturnType<typeof vi.fn>;
    finish: ReturnType<typeof vi.fn>;
    listSessions: ReturnType<typeof vi.fn>;
    notify: ReturnType<typeof vi.fn>;
    setDocumentTitle: ReturnType<typeof vi.fn>;
  };
}

const users = [{ id: "u1", name: "Alice", bindings: [{ scriptInstanceId: "s1" }] }];
const scripts = [{ id: "s1", name: "Script One" }];

describe("useConfigEditFlow restore semantics", () => {
  it("Case A: restores the locked editing UI without POSTing action:start", async () => {
    const adapters = createAdapters({
      listSessions: vi.fn().mockResolvedValue([{ userId: "u1", scriptId: "s1", editMode: "reuse" }]),
    });
    const flow = useConfigEditFlow(adapters);

    await flow.restoreExisting(users, scripts);

    expect(flow.configEdit.value).toEqual({
      userId: "u1",
      scriptId: "s1",
      userName: "Alice",
      scriptName: "Script One",
      mode: "reuse",
    });
    expect(flow.isOpen.value).toBe(true);
    expect(adapters.start).not.toHaveBeenCalled();
    expect(adapters.createRequesterWindowToken).not.toHaveBeenCalled();
    expect(adapters.setDocumentTitle).not.toHaveBeenCalled();
  });

  it("Case A2: restore() itself never starts a transaction or mutates the title", () => {
    const adapters = createAdapters();
    const flow = useConfigEditFlow(adapters);

    flow.restore({ userId: "u1", scriptId: "s1", userName: "Alice", scriptName: "Script One", mode: "normal" });

    expect(adapters.start).not.toHaveBeenCalled();
    expect(adapters.setDocumentTitle).not.toHaveBeenCalled();
    expect(flow.configEdit.value?.mode).toBe("normal");
  });

  it("Case B: finishing a restored session POSTs done exactly once", async () => {
    const adapters = createAdapters({
      listSessions: vi.fn().mockResolvedValue([{ userId: "u1", scriptId: "s1" }]),
    });
    const flow = useConfigEditFlow(adapters);
    await flow.restoreExisting(users, scripts);

    await flow.finish("done");

    expect(adapters.finish).toHaveBeenCalledTimes(1);
    expect(adapters.finish).toHaveBeenCalledWith("u1", "s1", "done");
    expect(adapters.start).not.toHaveBeenCalled();
    expect(flow.configEdit.value).toBeNull();
  });

  it("Case C: cancelling a restored session POSTs cancel exactly once", async () => {
    const adapters = createAdapters({
      listSessions: vi.fn().mockResolvedValue([{ userId: "u1", scriptId: "s1", editMode: "reuse" }]),
    });
    const flow = useConfigEditFlow(adapters);
    await flow.restoreExisting(users, scripts);

    await flow.finish("cancel");

    expect(adapters.finish).toHaveBeenCalledTimes(1);
    expect(adapters.finish).toHaveBeenCalledWith("u1", "s1", "cancel");
    expect(flow.configEdit.value).toBeNull();
  });

  it("Case D: a failing sessions API is silent and leaves the page usable", async () => {
    const adapters = createAdapters({ listSessions: vi.fn().mockRejectedValue(new Error("offline")) });
    const flow = useConfigEditFlow(adapters);

    await expect(flow.restoreExisting(users, scripts)).resolves.toBeUndefined();

    expect(flow.isOpen.value).toBe(false);
    expect(adapters.notify).not.toHaveBeenCalled();
    expect(adapters.start).not.toHaveBeenCalled();
  });

  it("Case D2: a session that matches nothing stays silent", async () => {
    const adapters = createAdapters({
      listSessions: vi.fn().mockResolvedValue([{ userId: "u-unknown", scriptId: "s-unknown" }]),
    });
    const flow = useConfigEditFlow(adapters);

    await flow.restoreExisting(users, scripts);

    expect(flow.isOpen.value).toBe(false);
    expect(adapters.notify).not.toHaveBeenCalled();
  });
});

describe("useConfigEditFlow start semantics", () => {
  it("open() with a snapshot starts a normal transaction once", async () => {
    const adapters = createAdapters({ getStatus: vi.fn().mockResolvedValue({ hasSnapshot: true }) });
    const flow = useConfigEditFlow(adapters);

    await flow.open({ userId: "u1", scriptId: "s1", userName: "Alice", scriptName: "Script One" }, true);

    expect(adapters.start).toHaveBeenCalledTimes(1);
    expect(adapters.start.mock.calls[0][2]).toMatchObject({ action: "start", mode: "normal" });
    expect(flow.configEdit.value?.mode).toBe("normal");
  });

  it("open() without a snapshot shows the first-edit chooser instead of starting", async () => {
    const adapters = createAdapters({ getStatus: vi.fn().mockResolvedValue({ hasSnapshot: false }) });
    const flow = useConfigEditFlow(adapters);

    await flow.open({ userId: "u1", scriptId: "s1", userName: "Alice", scriptName: "Script One" }, false);

    expect(adapters.start).not.toHaveBeenCalled();
    expect(flow.configChooser.value).toMatchObject({ userId: "u1", scriptId: "s1", freshAvailable: false });
  });

  it("a candidate mismatch surfaces the candidate chooser without a locked session", async () => {
    const mismatch = Object.assign(new Error("mismatch"), {
      code: "config_input_mismatch",
      data: { inputName: "configPath", candidates: ["a.json", "b.json"] },
    });
    const adapters = createAdapters({
      getStatus: vi.fn().mockResolvedValue({ hasSnapshot: true }),
      start: vi.fn().mockRejectedValue(mismatch),
    });
    const flow = useConfigEditFlow(adapters);

    await flow.open({ userId: "u1", scriptId: "s1", userName: "Alice", scriptName: "Script One" }, true);

    expect(flow.configEdit.value).toBeNull();
    expect(flow.configCandidates.value).toMatchObject({ inputName: "configPath", candidates: ["a.json", "b.json"] });
  });
});
