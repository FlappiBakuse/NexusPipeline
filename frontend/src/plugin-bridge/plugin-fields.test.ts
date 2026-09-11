import { describe, expect, it } from "vitest";
import { validateRequiredPluginFields } from "./plugin-fields";

/** 声明式多选字段：隐藏值载体保存 JSON 数组，字段根元素本身不是载体。 */
function multiSelectElement(carrierValue: string): Element {
  const carrier = { dataset: { nxpSelectMultiple: "true" }, value: carrierValue, id: "games" };
  return {
    matches: () => false,
    querySelector: () => carrier,
    id: "games",
    value: "",
  } as unknown as Element;
}

describe("plugin declarative fields", () => {
  it("reports required declarative fields that still hold no value", () => {
    const container = { querySelector: () => multiSelectElement("[]") } as unknown as Element;

    const invalid: string[] = [];
    const valid = validateRequiredPluginFields(
      container,
      [{ key: "games", type: "multi-select", required: true, label: "签到游戏" }],
      {},
      "data-plugin-field",
      input => invalid.push(input.id),
      () => {},
    );

    expect(valid).toBe(false);
    expect(invalid).toEqual(["games"]);
  });

  it("accepts a required multi-select whose value carrier holds selections", () => {
    const container = { querySelector: () => multiSelectElement('["gi","hsr"]') } as unknown as Element;

    const checked: string[] = [];
    const valid = validateRequiredPluginFields(
      container,
      [{ key: "games", type: "multi-select", required: true, label: "签到游戏" }],
      {},
      "data-plugin-field",
      () => {},
      input => checked.push(input.id),
    );

    expect(valid).toBe(true);
    expect(checked).toEqual(["games"]);
  });

  it("accepts a required secret that is already configured", () => {
    const element = { id: "token", value: "", matches: () => false } as unknown as Element;
    const container = { querySelector: () => element } as unknown as Element;
    const valid = validateRequiredPluginFields(
      container,
      [{ key: "token", type: "secret", required: true, label: "令牌" }],
      { token: { configured: true } },
    );
    expect(valid).toBe(true);
  });
});
