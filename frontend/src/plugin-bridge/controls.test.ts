import { describe, expect, it, vi } from "vitest";
import { colorControlMarkup, numberControlMarkup, selectControlMarkup } from "./controls";

describe("plugin bridge controls", () => {
  it("keeps a hidden value carrier and removes the native select", () => {
    const markup = selectControlMarkup(
      "queue",
      "default",
      [
        { value: "default", label: "默认队列" },
        { value: "night", label: "夜间队列" },
      ],
      'data-action="choose-queue"',
      "选择队列",
    );

    expect(markup).toMatch(/data-nxp-select/);
    expect(markup).toMatch(/type="hidden"[^>]*data-nxp-select-value/);
    expect(markup).toMatch(/role="listbox"/);
    expect(markup).toMatch(/data-nxp-select-option/);
    expect(markup).toMatch(/aria-label="选择队列"/);
    expect(markup).not.toMatch(/<select\b|<option\b/u);
  });

  it("uses a text input with accessible step buttons for the number control", () => {
    const number = numberControlMarkup("retry", 2, 'min="0" max="5" step="1"', "重试次数");

    expect(number).toMatch(/type="text"[^>]*inputmode="decimal"/);
    expect(number).toMatch(/data-nxp-step="increment"/);
    expect(number).toMatch(/data-nxp-step="decrement"/);
    expect(number).not.toMatch(/type="number"/u);
  });

  it("exposes only approved native carriers for the color control", () => {
    const color = colorControlMarkup("accent", "#abc");

    expect(color).toMatch(/data-nxp-color-trigger/);
    expect(color).toMatch(/class="sr-only" type="color"/);
    expect(color).toMatch(/data-nxp-color-value="#aabbcc"/);
  });

  it("installs the shared control event delegation only once", async () => {
    const addEventListener = vi.spyOn(document, "addEventListener");
    const { installPluginControlEvents } = await import("./controls");
    installPluginControlEvents();
    const callsAfterFirst = addEventListener.mock.calls.length;
    installPluginControlEvents();
    expect(addEventListener.mock.calls.length).toBe(callsAfterFirst);
    addEventListener.mockRestore();
    delete document.documentElement.dataset.nxpControlsBound;
  });
});
