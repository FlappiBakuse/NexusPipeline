import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import GlobalUserCard from "./GlobalUserCard.vue";
vi.mock("../../platform/i18n", () => ({ t: (key: string) => key }));
const props = { user: { id: "user", name: "User" }, badges: [], nextLabel: "", initials: "U", translate: (key: string) => key };
describe("specialized task badge", () => {
  it("distinguishes completed evidence gaps from no runs and opens history by keyboard", async () => {
    const wrapper = mount(GlobalUserCard, { props: { ...props, taskSummary: { userId: "user", tone: "muted", reason: "latest", recordId: "record" } } });
    const badge = wrapper.get('[role="button"]');
    expect(badge.text()).toContain("tasks.summary.latest");
    await badge.trigger("keydown", { key: "Enter" });
    expect(window.location.hash).toBe("#/history?recordId=record");
    await wrapper.setProps({ taskSummary: { userId: "user", tone: "ok", reason: "latest", recordId: "record" } });
    expect(badge.text()).toContain("tasks.summary.ok");
    await wrapper.setProps({ taskSummary: { userId: "user", tone: "ok", reason: "latest", recordId: "record", admissionState: "blocked", admissionRecordId: "blocked-record" } });
    const readiness = wrapper.findAll('[role="button"]')[1];
    expect(readiness.text()).toContain("tasks.readiness.blocked");
    await readiness.trigger("click");
    expect(window.location.hash).toBe("#/history?recordId=blocked-record");
    await wrapper.setProps({ taskSummary: undefined });
    expect(badge.text()).toContain("tasks.summary.not_run");
    await badge.trigger("click"); expect(wrapper.emitted("manage")).toEqual([[props.user]]);
    wrapper.unmount(); window.location.hash = "";
  });
});
