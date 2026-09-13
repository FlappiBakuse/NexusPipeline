import { describe, expect, it } from "vitest";
import { runtimeLabel, runtimeTone, storeActionNotice, storeActions, storeStatusLabel } from "./pluginStatusView";

const t = (key: string, _args?: Record<string, unknown>, fallback?: string) => fallback || key;

describe("pluginStatusView", () => {
  it("shows host incompatibility for a manually installed plugin", () => {
    const plugin = {
      name: "example-plugin",
      state: "Incompatible",
      runtimeErrorCode: "plugin_incompatible_host",
      minHostVersion: "0.16.0",
      configuredEnabled: true,
    };

    expect(runtimeLabel(plugin, t)).toBe("plugins.incompatible_host");
    expect(runtimeTone(plugin)).toBe("bad");
  });

  it("keeps API incompatibility distinguishable from a host version requirement", () => {
    expect(runtimeLabel({ state: "Incompatible", runtimeErrorCode: "plugin_incompatible_api" }, t)).toBe("plugins.incompatible");
    expect(storeActionNotice({ status: "incompatible", compatible: false, compatibilityCode: "plugin_api_incompatible" }, t))
      .toBe("This plugin is incompatible with the current Plugin API");
  });

  it("removes store actions when the catalog requires a host upgrade", () => {
    const plugin = {
      name: "example-plugin",
      status: "update-requires-host-upgrade",
      installed: true,
      updateAvailable: true,
      minHostVersion: "0.16.0",
    };

    expect(storeStatusLabel(plugin, t)).toBe("plugins.update_requires_host_upgrade");
    expect(storeActionNotice(plugin, t)).not.toBe("");
    expect(storeActions(plugin, t)).toEqual([]);
  });
});
