import { nextTick } from "vue";
import { describe, expect, it } from "vitest";
import { NEXUS_PUBLIC_ELEMENTS, registerNexusElements } from "./register";

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

  it("registers the structural cards as public plugins elements", async () => {
    expect(Object.keys(NEXUS_PUBLIC_ELEMENTS)).toContain("nxp-section-card");
    expect(Object.keys(NEXUS_PUBLIC_ELEMENTS)).toContain("nxp-collapsible-card");

    const section = await mountElement("nxp-section-card", { title: "宿主分区", description: "分区说明", variant: "secondary" });
    expect(section.shadowRoot).toBeNull();
    expect(section.querySelector(".nxp-section-card-title")?.textContent).toBe("宿主分区");
    expect(section.querySelector(".nxp-section-card-description")?.textContent).toBe("分区说明");
    expect(section.querySelector(".nxp-section-card")?.classList.contains("is-secondary")).toBe(true);
    section.remove();

    const collapsible = await mountElement("nxp-collapsible-card", { title: "宿主折叠", "panel-id": "plugin-panel" });
    expect(collapsible.querySelector(".settings-card-title")?.textContent).toBe("宿主折叠");
    expect(collapsible.querySelector("[role='button'], button")?.getAttribute("aria-controls")).toBe("plugin-panel");
    collapsible.remove();
  });

  it("reports the toggle payload of the public collapsible card as an event detail list", async () => {
    const element = await mountElement("nxp-collapsible-card", { title: "插件折叠", "panel-id": "plugin-toggle-panel" });
    const details: unknown[] = [];
    element.addEventListener("toggle", (event) => details.push((event as unknown as CustomEvent).detail));

    const toggle = element.querySelector<HTMLButtonElement>(".nxp-collapsible-card-toggle");
    expect(toggle?.getAttribute("aria-expanded")).toBe("false");
    toggle?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    await nextTick();

    expect(details).toEqual([[true]]);
    element.remove();
  });
});
