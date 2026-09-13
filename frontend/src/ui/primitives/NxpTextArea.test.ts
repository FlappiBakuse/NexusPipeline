import { mount } from "@vue/test-utils";
import { nextTick } from "vue";
import { afterEach, describe, expect, it, vi } from "vitest";
import NxpTextArea from "./NxpTextArea.vue";

describe("NxpTextArea", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("keeps a real textarea while exposing an overlay scrollbar for overflowing content", async () => {
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      callback(0);
      return 1;
    });
    vi.stubGlobal("cancelAnimationFrame", () => undefined);

    const wrapper = mount(NxpTextArea, {
      props: { id: "notes", modelValue: "内容" },
    });
    await nextTick();
    const textarea = wrapper.get("textarea").element as HTMLTextAreaElement;
    Object.defineProperties(textarea, {
      clientHeight: { configurable: true, value: 100 },
      scrollHeight: { configurable: true, value: 300 },
    });
    textarea.dispatchEvent(new Event("scroll"));
    await nextTick();

    expect(wrapper.get("textarea").element).toBe(textarea);
    const thumb = wrapper.get("[role='scrollbar']");
    expect(thumb.attributes("aria-controls")).toBe("notes");

    await thumb.trigger("keydown", { key: "End" });
    expect(textarea.scrollTop).toBe(200);
  });
});
