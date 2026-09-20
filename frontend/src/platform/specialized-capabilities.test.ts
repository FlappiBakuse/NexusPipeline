import { describe, expect, it } from "vitest";
import { projectSpecializedCapabilities } from "./specialized-capabilities";

describe("projectSpecializedCapabilities", () => {
  it("projects each Host capability independently", () => {
    expect(projectSpecializedCapabilities(["emulator"])).toEqual({
      supportsEmulator: true,
      selfManagedPcLaunch: false,
      allowFreshConfig: true,
    });
    expect(projectSpecializedCapabilities(["self-managed-pc-launch", "no-fresh-config"])).toEqual({
      supportsEmulator: false,
      selfManagedPcLaunch: true,
      allowFreshConfig: false,
    });
  });

  it("uses safe defaults for empty or malformed declarations", () => {
    expect(projectSpecializedCapabilities([])).toEqual({
      supportsEmulator: false,
      selfManagedPcLaunch: false,
      allowFreshConfig: true,
    });
    expect(projectSpecializedCapabilities(null)).toEqual({
      supportsEmulator: false,
      selfManagedPcLaunch: false,
      allowFreshConfig: true,
    });
  });

  it("keeps existing boolean API projections compatible while capability arrays migrate", () => {
    expect(projectSpecializedCapabilities(undefined, { supportsEmulator: true, noFreshConfig: true })).toEqual({
      supportsEmulator: true,
      selfManagedPcLaunch: false,
      allowFreshConfig: false,
    });
  });
});
