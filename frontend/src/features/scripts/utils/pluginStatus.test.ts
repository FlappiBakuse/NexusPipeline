import { describe, expect, it } from "vitest";
import { pluginDisplayName, scriptPluginStatus, scriptPluginUnavailableMessage } from "./pluginStatus";

describe("script plugin status", () => {
  it("uses installed plugin metadata for the display name", () => {
    const plugins = [{ name: "example-plugin", displayName: "示例插件", kind: "data-specialized" }];

    expect(pluginDisplayName("example-plugin", plugins)).toBe("示例插件");
    expect(pluginDisplayName("Example-Plugin", plugins)).toBe("示例插件");
  });

  it("falls back to the recorded plugin id when the plugin is not installed", () => {
    const plugins = [{ name: "another-plugin", displayName: "另一个插件", kind: "data-specialized" }];

    expect(pluginDisplayName("example-plugin", plugins)).toBe("example-plugin");
    expect(pluginDisplayName("example-plugin")).toBe("example-plugin");
  });

  it("marks specialized scripts unavailable when the plugin is missing", () => {
    const status = scriptPluginStatus({ pluginType: "example-plugin", name: "日常" }, []);

    expect(status.specialized).toBe(true);
    expect(status.missing).toBe(true);
    expect(status.available).toBe(false);
    expect(status.displayName).toBe("example-plugin");
    expect(scriptPluginUnavailableMessage({ pluginType: "example-plugin", name: "日常" }, [])).not.toBe("");
  });

  it("keeps specialized scripts available while the plugin is active", () => {
    const plugins = [{ name: "example-plugin", displayName: "示例插件", kind: "data-specialized", state: "Active", runtimeEnabled: true }];
    const status = scriptPluginStatus({ pluginType: "example-plugin", name: "日常" }, plugins);

    expect(status.missing).toBe(false);
    expect(status.available).toBe(true);
    expect(scriptPluginUnavailableMessage({ pluginType: "example-plugin", name: "日常" }, plugins)).toBe("");
  });

  it("treats manual scripts without a plugin type as available", () => {
    const status = scriptPluginStatus({ name: "手动脚本" }, []);

    expect(status.specialized).toBe(false);
    expect(status.available).toBe(true);
    expect(status.displayName).toBe("");
  });
});
