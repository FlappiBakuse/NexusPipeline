import { beforeEach, describe, expect, it, vi } from "vitest";
const fixture = vi.hoisted(() => ({ auth: null as string | null, api: vi.fn() }));
vi.mock("./api", () => ({ api: fixture.api, readAuthToken: () => fixture.auth }));
import { getCapabilities, invalidateClientCapabilities, validateClientCapabilities } from "./client-capabilities";
import { setServiceTrafficPaused } from "./service-traffic";
function capabilities(connectionKind = "remote") {
  return { schemaVersion: 1, connectionKind, operations: {
    general: { allowed: true, denyReason: null },
    hostFilePicker: { allowed: connectionKind === "local", denyReason: connectionKind === "local" ? null : "host_file_picker_requires_local" },
    nativeConfigEditor: { allowed: connectionKind === "local", denyReason: connectionKind === "local" ? null : "native_config_editor_requires_local" },
  } };
}
describe("client capabilities", () => {
  beforeEach(() => { invalidateClientCapabilities(); fixture.auth = null; fixture.api.mockReset(); setServiceTrafficPaused(false); });
  it("validates the exact schema and never invents local permissions", () => {
    const result = validateClientCapabilities(capabilities());
    expect(result.operations.general.allowed).toBe(true);
    expect(result.operations.hostFilePicker.allowed).toBe(false);
    expect(Object.isFrozen(result.operations.hostFilePicker)).toBe(true);
    expect(() => validateClientCapabilities({ ...capabilities(), schemaVersion: 2 })).toThrow();
    const forged = capabilities(); forged.operations.hostFilePicker.allowed = true;
    expect(() => validateClientCapabilities(forged)).toThrow();
  });
  it("invalidates its memory cache when authentication or service connection changes", async () => {
    fixture.api.mockResolvedValue(capabilities("local"));
    const first = await getCapabilities(); expect(await getCapabilities()).toBe(first);
    expect(fixture.api).toHaveBeenCalledTimes(1);
    fixture.auth = "test-token"; await getCapabilities(); expect(fixture.api).toHaveBeenCalledTimes(2);
    setServiceTrafficPaused(true); setServiceTrafficPaused(false);
    fixture.api.mockResolvedValue(capabilities());
    expect((await getCapabilities()).connectionKind).toBe("remote");
    expect(fixture.api).toHaveBeenCalledTimes(3);
  });
  it("rejects an obsolete response after the instance cache was invalidated", async () => {
    let resolve!: (value: unknown) => void;
    fixture.api.mockImplementation(() => new Promise(done => { resolve = done; }));
    const pending = getCapabilities(); invalidateClientCapabilities(); resolve(capabilities("local"));
    await expect(pending).rejects.toMatchObject({ name: "AbortError" });
  });
});
