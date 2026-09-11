import { describe, expect, it, vi } from "vitest";
import { createRootProbe, shouldProbeSpecializedRoot } from "./scriptProbe";

describe("shouldProbeSpecializedRoot", () => {
  it("requires both a plugin type and a root path", () => {
    expect(shouldProbeSpecializedRoot("hoyolab", "D:/Game")).toBe(true);
    expect(shouldProbeSpecializedRoot("", "D:/Game")).toBe(false);
    expect(shouldProbeSpecializedRoot("hoyolab", "   ")).toBe(false);
    expect(shouldProbeSpecializedRoot(null, undefined)).toBe(false);
  });
});

describe("createRootProbe", () => {
  it("skips the request when the editor is not a specialized script", async () => {
    const request = vi.fn().mockResolvedValue(undefined);
    const probe = createRootProbe({ request, onError: vi.fn() });
    await probe.probe("", "D:/Game");
    await probe.probe("hoyolab", "");
    expect(request).not.toHaveBeenCalled();
  });

  it("trims the payload before requesting", async () => {
    const request = vi.fn().mockResolvedValue(undefined);
    const probe = createRootProbe({ request, onError: vi.fn() });
    await probe.probe("  hoyolab  ", "  D:/Game  ");
    expect(request).toHaveBeenCalledWith({ pluginType: "hoyolab", rootPath: "D:/Game", inputs: {} });
  });

  it("only surfaces the failure of the latest change", async () => {
    let rejectFirst: (reason: unknown) => void = () => {};
    const request = vi
      .fn()
      .mockImplementationOnce(() => new Promise((_resolve, reject) => { rejectFirst = reject; }))
      .mockRejectedValueOnce(new Error("stale"));
    const onError = vi.fn();
    const probe = createRootProbe({ request, onError });

    const first = probe.probe("hoyolab", "D:/First");
    const second = probe.probe("hoyolab", "D:/Second");
    await second;
    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError).toHaveBeenCalledWith(expect.objectContaining({ message: "stale" }));

    rejectFirst(new Error("superseded"));
    await first;
    expect(onError).toHaveBeenCalledTimes(1);
  });

  it("suppresses in-flight failures after invalidate", async () => {
    let rejectPending: (reason: unknown) => void = () => {};
    const request = vi.fn().mockImplementation(() => new Promise((_resolve, reject) => { rejectPending = reject; }));
    const onError = vi.fn();
    const probe = createRootProbe({ request, onError });

    const pending = probe.probe("hoyolab", "D:/Game");
    probe.invalidate();
    rejectPending(new Error("closed"));
    await pending;
    expect(onError).not.toHaveBeenCalled();
  });
});
