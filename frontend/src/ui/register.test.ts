import { nextTick } from "vue";
import { describe, expect, it } from "vitest";
import { registerNexusElements } from "./register";

describe("public Nexus elements", () => {
  it("keeps registered controls in the host light DOM", async () => {
    registerNexusElements();
    const element = document.createElement("nxp-select");
    document.body.append(element);
    await customElements.whenDefined("nxp-select");
    await nextTick();

    expect(element.shadowRoot).toBeNull();
    expect(element.querySelector(".nxp-select-trigger")).not.toBeNull();

    element.remove();
  });
});
