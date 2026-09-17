import { h, nextTick } from "vue";
import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import NxpActionGroup from "./NxpActionGroup.vue";
import NxpDateRangePicker from "./NxpDateRangePicker.vue";
import NxpDragHandle from "./NxpDragHandle.vue";
import NxpEntityRow from "./NxpEntityRow.vue";
import NxpSortableList from "./NxpSortableList.vue";
import NxpField from "../primitives/NxpField.vue";

describe("public composite UI", () => {
  it("renders named entity slots and centralized actions without domain assumptions", () => {
    const wrapper = mount(NxpEntityRow, {
      props: { as: "article", itemId: "queue-1" },
      slots: {
        leading: h(NxpDragHandle, { label: "重排" }),
        content: h("strong", { class: "entity-name" }, "队列"),
        meta: h("span", { class: "entity-meta" }, "2 tasks"),
        actions: h(NxpActionGroup, null, { default: () => h("button", { type: "button" }, "编辑") }),
      },
    });

    expect(wrapper.element.tagName).toBe("ARTICLE");
    expect(wrapper.attributes("data-dnd-id")).toBe("queue-1");
    expect(wrapper.get(".entity-name").text()).toBe("队列");
    expect(wrapper.get(".entity-meta").text()).toBe("2 tasks");
    expect(wrapper.get(".nxp-action-group").text()).toBe("编辑");
  });

  it("exposes the contents slot layout for grid-based entity consumers", () => {
    const wrapper = mount(NxpEntityRow, {
      props: { slotLayout: "contents" },
      slots: {
        leading: "leading",
        content: "content",
        actions: "actions",
      },
    });

    expect(wrapper.get(".nxp-entity-row-leading").classes()).toContain("is-contents");
    expect(wrapper.get(".nxp-entity-row-actions").classes()).toContain("is-contents");
  });

  it("associates field labels and exposes named help/error content", () => {
    const wrapper = mount(NxpField, {
      props: { label: "名称", for: "name-input", required: true, error: "必填" },
      slots: { help: "帮助", description: "说明" },
    });

    expect(wrapper.get("label").attributes("for")).toBe("name-input");
    expect(wrapper.get(".nxp-field-label").text()).toContain("名称");
    expect(wrapper.get(".nxp-field-help").text()).toBe("帮助");
    expect(wrapper.get(".nxp-field-description").text()).toBe("说明");
    expect(wrapper.get(".nxp-field-error").attributes("role")).toBe("alert");
  });

  it("uses the shared sortable lifecycle and emits a complete order", async () => {
    const wrapper = mount(NxpSortableList, {
      slots: {
        default: () => [
          h("div", { "data-dnd-id": "a" }, [h(NxpDragHandle, { label: "a" })]),
          h("div", { "data-dnd-id": "b" }, [h(NxpDragHandle, { label: "b" })]),
        ],
      },
    });

    await wrapper.get('[data-dnd-id="b"] .drag-handle').trigger("keydown", { key: "ArrowUp" });
    await nextTick();

    expect(wrapper.findAll("[data-dnd-id]").map(item => item.attributes("data-dnd-id"))).toEqual(["a", "b"]);
    expect(wrapper.emitted("reorder")).toEqual([[ ["b", "a"], "b" ]]);
    wrapper.unmount();
  });

  it("rejects duplicate keys and disabled sorting", async () => {
    const wrapper = mount(NxpSortableList, {
      props: { disabled: true },
      slots: { default: () => ["a", "b"].map(id => h("div", { "data-dnd-id": id }, [h(NxpDragHandle, { label: id })])) },
    });
    await wrapper.get('[data-dnd-id="b"] button').trigger("keydown", { key: "ArrowUp" });
    expect(wrapper.emitted("reorder")).toBeUndefined();
    await wrapper.setProps({ disabled: false });
    wrapper.get('[data-dnd-id="b"]').element.setAttribute("data-dnd-id", "a");
    await wrapper.findAll("button")[1].trigger("keydown", { key: "ArrowUp" });
    expect(wrapper.emitted("reorder")).toBeUndefined();
    wrapper.unmount();
  });

  it("shows committed dates after dismissing a draft and follows external updates", async () => {
    const wrapper = mount(NxpDateRangePicker, { props: { open: true, from: "2026-09-05", to: "2026-09-12", maxDate: "2026-09-17", locale: "en-US" } });
    await wrapper.get('button[aria-label="Sep 8, 2026"]').trigger("click");
    await wrapper.setProps({ open: false });
    expect(wrapper.get("[data-nxp-date-range-display]").text()).toContain("2026/09/05");
    expect(wrapper.emitted("apply")).toBeUndefined();
    await wrapper.setProps({ from: "2026-09-01", to: "2026-09-02" });
    expect(wrapper.get("[data-nxp-date-range-display]").text()).toContain("2026/09/01");
    wrapper.unmount();
  });

  it("keeps date drafts local until apply and rejects future selections", async () => {
    const wrapper = mount(NxpDateRangePicker, {
      props: {
        open: true,
        from: "2026-09-05",
        to: "2026-09-12",
        maxDate: "2026-09-17",
        locale: "en-US",
        weekdays: ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"],
      },
    });

    expect(wrapper.get("[data-nxp-date-range-display]").text()).toContain("2026/09/05");
    const future = wrapper.findAll(".nxp-date-range-day").find(button => button.text() === "18" && button.attributes("disabled") !== undefined);
    expect(future).toBeDefined();

    await wrapper.get(".nxp-date-range-day.is-start").trigger("click");
    await wrapper.get(".nxp-date-range-day.is-end").trigger("click");
    expect(wrapper.emitted("apply")).toBeUndefined();

    await wrapper.get(".nxp-date-range-footer .nxp-button").trigger("click");
    expect(wrapper.emitted("apply")).toHaveLength(1);
    expect(wrapper.emitted("close")).toHaveLength(1);
  });
});
