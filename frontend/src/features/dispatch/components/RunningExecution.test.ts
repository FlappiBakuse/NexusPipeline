import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import RunningExecution from "./RunningExecution.vue";

function mountExecution() {
  return mount(RunningExecution, {
    props: {
      running: [
        { id: "run-1", targetName: "脚本一", kind: "script", mode: "manual", logTail: ["one"] },
        { id: "run-2", targetName: "脚本二", kind: "script", mode: "manual", logTail: ["two"] },
      ],
      systemAction: null,
      busy: false,
      executionPreviewLayoutEnabled: false,
    },
  });
}

function heightOf(handle: { element: Element }) {
  return Number(handle.element.parentElement?.getAttribute("data-log-height"));
}

describe("RunningExecution log resizer", () => {
  it("keeps each run height independent and supports keyboard adjustment", async () => {
    const wrapper = mountExecution();
    const first = wrapper.get("[data-testid='run-log-resize-handle-run-1']");
    const second = wrapper.get("[data-testid='run-log-resize-handle-run-2']");
    const secondInitial = heightOf(second);

    await first.trigger("keydown", { key: "ArrowDown" });
    expect(heightOf(first)).toBeGreaterThan(180);
    expect(heightOf(second)).toBe(secondInitial);

    await first.trigger("keydown", { key: "Home" });
    expect(heightOf(first)).toBe(180);
    await first.trigger("keydown", { key: "End" });
    expect(first.attributes("aria-valuenow")).toBe(first.attributes("aria-valuemax"));
    wrapper.unmount();
  });

  it("exposes a pointer-friendly separator for each run", () => {
    const wrapper = mountExecution();
    const handles = wrapper.findAll("[role='separator']");

    expect(handles).toHaveLength(2);
    expect(handles.every(handle => handle.attributes("aria-orientation") === "horizontal")).toBe(true);
    expect(handles.every(handle => handle.attributes("tabindex") === "0")).toBe(true);
    wrapper.unmount();
  });
});
