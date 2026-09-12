import { nextTick } from "vue";
import { afterEach, describe, expect, it } from "vitest";
import { clearFieldError, setRequiredFieldError } from "../platform/toast";
import { registerNexusElements } from "../ui/register";
import { createPluginFieldControl, type PluginFieldControl } from "./controls";
import type { PluginField } from "./types";

/**
 * 声明式插件表单控件的表驱动验收：控件必须是宿主公开的 nxp-* 元素，
 * 初始值、约束、改动后的插件载荷与清理行为都通过真实交互验证。
 */

function field(type: string, extra: Partial<PluginField> = {}): PluginField {
  return { key: "value", type, label: "字段", ...extra };
}

const mounted: PluginFieldControl[] = [];

async function mountControl(type: string, value: unknown, extra: Partial<PluginField> = {}): Promise<PluginFieldControl> {
  registerNexusElements();
  const container = document.createElement("div");
  document.body.append(container);
  const control = createPluginFieldControl(field(type, extra), value, `plugin-fixture-settings-${type}`, container);
  mounted.push(control);
  await customElements.whenDefined(control.element.tagName.toLowerCase());
  await nextTick();
  return control;
}

async function changeCarrier(control: PluginFieldControl, next: string): Promise<void> {
  const carrier = control.carrier as HTMLInputElement;
  carrier.value = next;
  carrier.dispatchEvent(new Event("change", { bubbles: true }));
  await nextTick();
}

afterEach(async () => {
  mounted.splice(0).forEach(control => control.destroy());
  await nextTick();
  await nextTick();
});

describe("plugin bridge field controls", () => {
  const cases: Array<{ type: string; tag: string; carrier: string; initial: unknown; next: string; expected: unknown }> = [
    { type: "text", tag: "nxp-text-input", carrier: "input.nxp-input", initial: "hello", next: "world", expected: "world" },
    { type: "url", tag: "nxp-text-input", carrier: "input.nxp-input", initial: "https://example.com", next: "https://nexus.test", expected: "https://nexus.test" },
    { type: "secret", tag: "nxp-text-input", carrier: "input.nxp-input", initial: undefined, next: "s3cret", expected: { action: "set", value: "s3cret" } },
    { type: "status", tag: "nxp-text-input", carrier: "input.nxp-input", initial: "运行中", next: "运行中", expected: "运行中" },
    { type: "textarea", tag: "nxp-text-area", carrier: "textarea.nxp-textarea", initial: "第一行", next: "第二行", expected: "第二行" },
    { type: "number", tag: "nxp-number-input", carrier: "[data-nxp-number-value]", initial: 3, next: "7", expected: 7 },
    { type: "range", tag: "nxp-range", carrier: "input.nxp-range", initial: 20, next: "35", expected: 35 },
    { type: "color", tag: "nxp-color-picker", carrier: ".nxp-color-value", initial: "#336699", next: "#aabbcc", expected: "#aabbcc" },
  ];

  for (const testCase of cases) {
    it(`renders ${testCase.type} with the public ${testCase.tag} element`, async () => {
      const control = await mountControl(testCase.type, testCase.initial);
      expect(control.element.tagName.toLowerCase()).toBe(testCase.tag);
      expect(control.carrier.matches(testCase.carrier)).toBe(true);
      expect(control.carrier.id).toBe(`plugin-fixture-settings-${testCase.type}`);
      expect(control.carrier.dataset.pluginType).toBe(testCase.type);

      await changeCarrier(control, testCase.next);
      expect(control.value()).toEqual(testCase.expected);
      control.destroy();
    });
  }

  it("passes constraints and read-only state to the number element", async () => {
    const control = await mountControl("number", 5, { min: 1, max: 9, step: 2, readOnly: true });
    const carrier = control.carrier as HTMLInputElement;

    expect(carrier.getAttribute("min")).toBe("1");
    expect(carrier.getAttribute("max")).toBe("9");
    expect(carrier.getAttribute("step")).toBe("2");
    expect(carrier.disabled).toBe(true);
    control.destroy();
  });

  it("keeps a secret field empty and reports the keep action when untouched", async () => {
    const control = await mountControl("secret", { configured: true });
    expect((control.carrier as HTMLInputElement).value).toBe("");
    expect((control.carrier as HTMLInputElement).type).toBe("password");
    expect((control.carrier as HTMLInputElement).placeholder).not.toBe("");
    expect(control.value()).toEqual({ action: "keep" });
    control.destroy();
  });

  it("toggles the switch through the public element and reports a boolean payload", async () => {
    const control = await mountControl("switch", false);
    const toggle = control.carrier as HTMLButtonElement;
    expect(toggle.getAttribute("aria-pressed")).toBe("false");

    toggle.click();
    await nextTick();
    expect(toggle.getAttribute("aria-pressed")).toBe("true");
    expect(control.value()).toBe(true);
    control.destroy();
  });

  it("selects a single option through the public select element", async () => {
    const control = await mountControl("select", "night", {
      options: [{ value: "default", label: "默认队列" }, { value: "night", label: "夜间队列" }],
    });
    expect((control.carrier as HTMLInputElement).value).toBe("night");

    control.element.querySelector<HTMLButtonElement>(".nxp-select-trigger")?.click();
    await nextTick();
    const option = Array.from(document.querySelectorAll<HTMLButtonElement>("[data-nxp-select-option]"))
      .find(item => item.dataset.value === "default");
    option?.click();
    await nextTick();

    expect(control.value()).toBe("default");
    expect(control.labelFor).toBe("plugin-fixture-settings-select-trigger");
    control.destroy();
  });

  it("collects multiple selections from the public select element", async () => {
    const control = await mountControl("multi-select", ["a"], {
      options: [{ value: "a", label: "甲" }, { value: "b", label: "乙" }, { value: "c", label: "丙" }],
    });
    expect(control.value()).toEqual(["a"]);

    control.element.querySelector<HTMLButtonElement>(".nxp-select-trigger")?.click();
    await nextTick();
    const option = Array.from(document.querySelectorAll<HTMLButtonElement>("[data-nxp-select-option]"))
      .find(item => item.dataset.value === "b");
    option?.click();
    await nextTick();

    expect(control.value()).toEqual(["a", "b"]);
    control.destroy();
  });

  it("projects required-field errors onto the component carrier", async () => {
    const control = await mountControl("number", "");
    setRequiredFieldError(control.carrier.id);
    expect(document.getElementById(control.carrier.id)).toBe(control.carrier);
    expect(control.carrier.classList.contains("field-error")).toBe(true);
    expect(control.carrier.getAttribute("aria-invalid")).toBe("true");

    clearFieldError(control.carrier.id);
    expect(control.carrier.classList.contains("field-error")).toBe(false);
    control.destroy();
  });

  it("removes the element, listeners and teleported popover on cleanup", async () => {
    const baseline = document.querySelectorAll("[data-nxp-select-option]").length;
    const control = await mountControl("select", "a", { options: [{ value: "a", label: "甲" }] });
    control.element.querySelector<HTMLButtonElement>(".nxp-select-trigger")?.click();
    await nextTick();
    expect(document.querySelectorAll("[data-nxp-select-option]").length).toBeGreaterThan(baseline);

    control.destroy();
    await nextTick();
    await nextTick();

    expect(control.element.isConnected).toBe(false);
    expect(document.querySelectorAll("[data-nxp-select-option]").length).toBe(baseline);
    const before = control.value();
    await changeCarrier(control, "b");
    expect(control.value()).toBe(before);
  });
});
