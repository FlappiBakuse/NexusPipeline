import { nextTick } from "vue";
import { describe, expect, it } from "vitest";
import { registerNexusElements } from "./register";

async function mountElement(tagName: string, attributes: Record<string, string> = {}) {
  registerNexusElements();
  const element = document.createElement(tagName);
  for (const [name, value] of Object.entries(attributes)) element.setAttribute(name, value);
  document.body.append(element);
  await customElements.whenDefined(tagName);
  await nextTick();
  return element;
}

describe("public Nexus elements", () => {
  it("keeps registered controls in the host light DOM", async () => {
    const element = await mountElement("nxp-select");

    expect(element.shadowRoot).toBeNull();
    expect(element.querySelector(".nxp-select-trigger")).not.toBeNull();

    element.remove();
  });

  it("marks the path picker buttons as tooltip-excluded controls", async () => {
    const element = await mountElement("nxp-path-picker", { kind: "file-or-folder" });

    const triggers = Array.from(element.querySelectorAll("button.nxp-path-trigger"));
    expect(triggers).toHaveLength(2);
    for (const trigger of triggers) {
      expect(trigger.hasAttribute("data-path-trigger")).toBe(true);
    }

    element.remove();
  });

  it("marks the number stepper buttons as tooltip-excluded controls", async () => {
    const element = await mountElement("nxp-number-input");

    const steps = Array.from(element.querySelectorAll("[data-nxp-step]"));
    expect(steps).toHaveLength(2);

    element.remove();
  });

  it("exposes a focusable button as the select help anchor", async () => {
    const element = await mountElement("nxp-select");

    expect(element.querySelector(".nxp-select-trigger")?.tagName).toBe("BUTTON");

    element.remove();
  });
});
