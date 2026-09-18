import { createApp, h, nextTick, ref } from "vue";
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
  it("renders named public slots for fields, rows and page actions", async () => {
    registerNexusElements();
    const root = document.createElement("div");
    root.innerHTML = '<nxp-field><span slot="error">字段错误</span></nxp-field><nxp-entity-row><span slot="meta">摘要</span><nxp-button slot="actions" label="编辑"></nxp-button></nxp-entity-row><nxp-page-header title="标题"><nxp-button slot="actions" label="保存"></nxp-button></nxp-page-header>';
    document.body.append(root);
    await nextTick();
    try {
      expect(root.querySelector('[role="alert"]')?.textContent).toBe("字段错误");
      expect(root.querySelector("nxp-entity-row")?.textContent).toContain("摘要");
      expect([...root.querySelectorAll("button")].map(button => button.textContent?.trim())).toEqual(["编辑", "保存"]);
    } finally { root.remove(); }
  });
  it("renders nested sortable item slots", async () => {
    registerNexusElements();
    const element = document.createElement("nxp-collapsible-card");
    element.innerHTML = '<div><nxp-sortable-list class="test" aria-label="Items"><div data-dnd-id="a"><nxp-drag-handle label="A"></nxp-drag-handle></div><div data-dnd-id="b"><nxp-drag-handle label="B"></nxp-drag-handle></div></nxp-sortable-list></div>';
    document.body.append(element);
    await nextTick();
    try { expect(element.querySelectorAll("[data-dnd-id]")).toHaveLength(2); }
    finally { element.remove(); }
  });
  it("updates slotted lists after their external owner loads and reorders items", async () => {
    registerNexusElements();
    const items = ref<string[]>([]);
    const root = document.createElement("div");
    document.body.append(root);
    const app = createApp({ render: () => h("nxp-sortable-list", null, items.value.map(id => h("div", { key: id, "data-dnd-id": id }, id))) });
    app.mount(root);
    try {
      items.value = ["a", "b"];
      await nextTick();
      expect([...root.querySelectorAll("[data-dnd-id]")].map(node => node.textContent)).toEqual(["a", "b"]);
      items.value = ["b", "a"];
      await nextTick();
      expect([...root.querySelectorAll("[data-dnd-id]")].map(node => node.textContent)).toEqual(["b", "a"]);
    } finally { app.unmount(); root.remove(); }
  });
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
    expect(collapsible.querySelector(".nxp-collapsible-card-title")?.textContent).toBe("宿主折叠");
    expect(collapsible.querySelector("[role='button'], button")?.getAttribute("aria-controls")).toBe("plugin-panel");
    collapsible.remove();
  });

  it("registers generic field, row, action and sorting composites", async () => {
    expect(Object.keys(NEXUS_PUBLIC_ELEMENTS)).toEqual(expect.arrayContaining([
      "nxp-action-group",
      "nxp-date-range-picker",
      "nxp-drag-handle",
      "nxp-entity-row",
      "nxp-schedule-card",
      "nxp-sortable-list",
    ]));

    const handle = await mountElement("nxp-drag-handle", { label: "重排" });
    expect(handle.querySelector("button.drag-handle")?.getAttribute("aria-label")).toBe("重排");
    handle.remove();

    const row = await mountElement("nxp-entity-row", { "item-id": "item-1" });
    expect(row.querySelector("[data-entity-row]")?.getAttribute("data-dnd-id")).toBe("item-1");
    row.remove();

    const schedule = await mountElement("nxp-schedule-card", {
      "item-id": "schedule-1",
      "data-dnd-id": "schedule-1",
      "panel-id": "schedule-panel-1",
      "summary-label": "定时 1",
      "summary-meta": "09:00 · 5 天",
      expanded: "",
    });
    expect(schedule.querySelector("[data-dnd-id]")?.getAttribute("data-dnd-id")).toBe("schedule-1");
    expect(schedule.querySelector(".nxp-schedule-summary")?.getAttribute("aria-expanded")).toBe("true");
    schedule.remove();
  });

  it("renders named schedule actions for public custom-element consumers", async () => {
    registerNexusElements();
    const schedule = document.createElement("nxp-schedule-card");
    schedule.setAttribute("item-id", "schedule-actions-1");
    schedule.setAttribute("panel-id", "schedule-actions-panel");
    schedule.setAttribute("days-label", "执行周期");
    schedule.setAttribute("time-label", "执行时间");
    schedule.setAttribute("expanded", "");
    const enabled = document.createElement("nxp-switch");
    enabled.setAttribute("slot", "actions");
    enabled.setAttribute("semantic-role", "switch");
    enabled.setAttribute("aria-label", "启用计划");
    const remove = document.createElement("nxp-button");
    remove.setAttribute("slot", "actions");
    remove.setAttribute("label", "删除定时");
    schedule.append(enabled, remove);
    document.body.append(schedule);

    await customElements.whenDefined("nxp-schedule-card");
    await customElements.whenDefined("nxp-switch");
    await customElements.whenDefined("nxp-button");
    await nextTick();

    expect(schedule.querySelector(".nxp-schedule-actions")).not.toBeNull();
    expect(schedule.querySelector(".nxp-schedule-actions nxp-switch")).toBe(enabled);
    expect(schedule.querySelector(".nxp-schedule-actions nxp-button")).toBe(remove);
    schedule.remove();
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
