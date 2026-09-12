import { describe, expect, it } from "vitest";
import { validateRequiredPluginFields } from "./plugin-fields";
import type { PluginFieldControl } from "./controls";

/** 校验只读取桥接层控件对象，因此用例用最小控件替身覆盖取值形态。 */
function control(value: unknown, id = "field"): PluginFieldControl {
  return {
    element: document.createElement("div"),
    type: "text",
    labelFor: id,
    carrier: { id } as unknown as HTMLElement,
    value: () => value,
    destroy: () => {},
  };
}

describe("plugin declarative fields", () => {
  it("reports required declarative fields that still hold no value", () => {
    const controls = new Map([["games", control([], "games")]]);

    const invalid: string[] = [];
    const valid = validateRequiredPluginFields(
      controls,
      [{ key: "games", type: "multi-select", required: true, label: "签到游戏" }],
      {},
      item => invalid.push(item.carrier.id),
      () => {},
    );

    expect(valid).toBe(false);
    expect(invalid).toEqual(["games"]);
  });

  it("accepts a required multi-select whose control holds selections", () => {
    const controls = new Map([["games", control(["gi", "hsr"], "games")]]);

    const checked: string[] = [];
    const valid = validateRequiredPluginFields(
      controls,
      [{ key: "games", type: "multi-select", required: true, label: "签到游戏" }],
      {},
      () => {},
      item => checked.push(item.carrier.id),
    );

    expect(valid).toBe(true);
    expect(checked).toEqual(["games"]);
  });

  it("accepts a required secret that is already configured", () => {
    const controls = new Map([["token", control({ action: "keep" }, "token")]]);
    const valid = validateRequiredPluginFields(
      controls,
      [{ key: "token", type: "secret", required: true, label: "令牌" }],
      { token: { configured: true } },
    );
    expect(valid).toBe(true);
  });

  it("requires a new value when a configured secret was cleared", () => {
    const controls = new Map([["token", control({ action: "keep" }, "token")]]);
    expect(validateRequiredPluginFields(
      controls,
      [{ key: "token", type: "secret", required: true, label: "令牌" }],
      { token: { configured: false } },
    )).toBe(false);
    expect(validateRequiredPluginFields(
      new Map([["token", control({ action: "set", value: "abc" }, "token")]]),
      [{ key: "token", type: "secret", required: true, label: "令牌" }],
      { token: { configured: false } },
    )).toBe(true);
  });

  it("ignores switch, status and read-only fields", () => {
    const controls = new Map([
      ["enabled", control(false, "enabled")],
      ["state", control("", "state")],
      ["note", control("", "note")],
    ]);
    const valid = validateRequiredPluginFields(controls, [
      { key: "enabled", type: "switch", required: true, label: "启用" },
      { key: "state", type: "status", required: true, label: "状态" },
      { key: "note", type: "text", required: true, readOnly: true, label: "备注" },
    ]);
    expect(valid).toBe(true);
  });
});
