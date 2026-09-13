import { mount } from "@vue/test-utils";
import { defineComponent, nextTick, ref } from "vue";
import { describe, expect, it } from "vitest";
import { vSortable } from "./sortable";

function testComponent() {
  return defineComponent({
    directives: { sortable: vSortable },
    setup() {
      const items = ref(["a", "b", "c"]);
      const onDrop = (ids: string[]) => { items.value = ids; };
      return { items, onDrop };
    },
    template: `<div v-sortable="{ onDrop }"><div v-for="item in items" :key="item" :data-dnd-id="item"><button class="drag-handle" type="button">{{ item }}</button></div></div>`,
  });
}

describe("vSortable", () => {
  it("moves the complete item with the keyboard and reports the new order", async () => {
    const wrapper = mount(testComponent());
    await wrapper.get('[data-dnd-id="b"] .drag-handle').trigger("keydown", { key: "ArrowUp" });
    await nextTick();
    expect(wrapper.findAll("[data-dnd-id]").map(item => item.attributes("data-dnd-id"))).toEqual(["b", "a", "c"]);
    expect(wrapper.find('[data-dnd-id="b"] .drag-handle').exists()).toBe(true);
    wrapper.unmount();
  });

  it("moves the whole card for pointer input and ignores a disabled handle", async () => {
    const wrapper = mount(testComponent());
    const container = wrapper.element as HTMLElement;
    const items = Array.from(container.children) as HTMLElement[];
    items.forEach((item, index) => {
      item.getBoundingClientRect = () => ({ top: index * 40, bottom: index * 40 + 40, left: 0, right: 200, width: 200, height: 40, x: 0, y: index * 40, toJSON() {} });
    });
    const pointer = (type: string, values: Record<string, number>) => {
      const event = new Event(type, { bubbles: true, cancelable: true });
      for (const [key, value] of Object.entries(values)) Object.defineProperty(event, key, { value });
      return event;
    };
    const handle = wrapper.get('[data-dnd-id="c"] .drag-handle').element;
    handle.dispatchEvent(pointer("pointerdown", { button: 0, pointerId: 1, clientX: 20, clientY: 100 }));
    container.dispatchEvent(pointer("pointermove", { pointerId: 1, clientX: 20, clientY: 0 }));
    expect((wrapper.get('[data-dnd-id="c"]').element as HTMLElement).style.transform).toContain("translate");
    container.dispatchEvent(pointer("pointerup", { pointerId: 1, clientX: 20, clientY: 0 }));
    await nextTick();
    expect(wrapper.findAll("[data-dnd-id]").map(item => item.attributes("data-dnd-id"))).toEqual(["c", "a", "b"]);

    const disabled = wrapper.get('[data-dnd-id="b"] .drag-handle').element;
    disabled.setAttribute("aria-disabled", "true");
    disabled.dispatchEvent(pointer("pointerdown", { button: 0, pointerId: 2, clientX: 20, clientY: 100 }));
    expect(wrapper.findAll("[data-dnd-id]").map(item => item.attributes("data-dnd-id"))).toEqual(["c", "a", "b"]);
    wrapper.unmount();
  });

  it("keeps the item attached to the pointer on both axes when configured", () => {
    const wrapper = mount(defineComponent({
      directives: { sortable: vSortable },
      setup() {
        return { items: ref(["a", "b"]) };
      },
      template: `<div v-sortable="{ axis: 'both' }"><div v-for="item in items" :key="item" :data-dnd-id="item"><button class="drag-handle" type="button">{{ item }}</button></div></div>`,
    }));
    const container = wrapper.element as HTMLElement;
    const item = wrapper.get('[data-dnd-id="b"]').element as HTMLElement;
    const handle = wrapper.get('[data-dnd-id="b"] .drag-handle').element;
    const pointer = (type: string, values: Record<string, number>) => {
      const event = new Event(type, { bubbles: true, cancelable: true });
      for (const [key, value] of Object.entries(values)) Object.defineProperty(event, key, { value });
      return event;
    };
    handle.dispatchEvent(pointer("pointerdown", { button: 0, pointerId: 3, clientX: 20, clientY: 60 }));
    container.dispatchEvent(pointer("pointermove", { pointerId: 3, clientX: 88, clientY: 96 }));
    expect(item.style.transform).toBe("translate(68px, 36px)");
    container.dispatchEvent(pointer("pointerup", { pointerId: 3, clientX: 88, clientY: 96 }));
    wrapper.unmount();
  });
});
