import { afterEach, describe, expect, it } from "vitest";
import { hideTooltip, initTooltips } from "./tooltip";

/**
 * 延时气泡帮助契约：
 * - `[data-help]` 容器内的可聚焦元件延迟显示提示；
 * - 路径选择按钮 `[data-path-trigger]` 与数字步进按钮 `[data-nxp-step]` 不继承所属字段的提示。
 */
describe("delayed help tooltip contract", () => {
  afterEach(() => {
    hideTooltip();
    document.body.innerHTML = "";
  });

  it("shows the field help for a focusable control and hides it on leave", async () => {
    initTooltips();
    const field = document.createElement("div");
    field.dataset.help = "字段说明";
    const input = document.createElement("input");
    input.type = "text";
    field.append(input);
    document.body.append(field);

    input.dispatchEvent(new FocusEvent("focusin", { bubbles: true }));
    input.focus();
    await new Promise(resolve => setTimeout(resolve, 720));

    const tooltip = document.body.querySelector(":scope > .nxp-tooltip");
    expect(tooltip?.textContent).toBe("字段说明");

    input.dispatchEvent(new FocusEvent("focusout", { bubbles: true }));
    expect(document.body.querySelector(":scope > .nxp-tooltip")).toBeNull();
  });

  it("suppresses the field help for path picker and number stepper buttons", async () => {
    initTooltips();
    const field = document.createElement("div");
    field.dataset.help = "字段说明";
    const input = document.createElement("input");
    input.type = "text";
    const pathTrigger = document.createElement("button");
    pathTrigger.type = "button";
    pathTrigger.dataset.pathTrigger = "true";
    const step = document.createElement("button");
    step.type = "button";
    step.dataset.nxpStep = "increment";
    field.append(input, pathTrigger, step);
    document.body.append(field);

    for (const control of [pathTrigger, step]) {
      control.dispatchEvent(new FocusEvent("focusin", { bubbles: true }));
      control.focus();
      await new Promise(resolve => setTimeout(resolve, 720));
      expect(document.body.querySelector(":scope > .nxp-tooltip"), `${control.tagName} ${control.dataset}`).toBeNull();
    }

    // 排除只针对操作按钮：同一容器内的文本输入仍然提供字段说明。
    input.dispatchEvent(new FocusEvent("focusin", { bubbles: true }));
    input.focus();
    await new Promise(resolve => setTimeout(resolve, 720));
    expect(document.body.querySelector(":scope > .nxp-tooltip")?.textContent).toBe("字段说明");
  });
});
