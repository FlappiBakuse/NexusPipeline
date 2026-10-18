import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { t } from "../../platform/i18n";
import NotificationEditor from "./NotificationEditor.vue";
import { newNotification, type NotificationTask } from "./notificationTypes";

const fixture = vi.hoisted(() => ({ api: vi.fn() }));
vi.mock("../../platform/api", () => ({ api: fixture.api, isAbortError: () => false }));
vi.mock("../../platform/service-recovery", () => ({ registerRecoveryDirtyGuard: () => () => {} }));
let wrapper: VueWrapper;
beforeEach(() => { fixture.api.mockReset().mockResolvedValue({}); });
afterEach(() => wrapper?.unmount());

function setup(task: NotificationTask | null = null) {
  wrapper = mount(NotificationEditor, { props: { open: true, task, type: "smtp" }, global: {
    stubs: { NxpModal: { template: "<section><slot /><slot name='footer' /></section>" } },
  } });
}
function input(key: string) { return wrapper.get(`input[aria-label="${t(key)}"]`); }
async function click(key: string) {
  await wrapper.findAll("button").find(button => button.text() === t(key))!.trigger("click");
  await flushPromises();
}
function savedTask(): NotificationTask {
  return { ...newNotification("smtp"), id: "saved-task", name: "Saved notification", configured: false,
    secrets: { smtpPassword: true, webhookUrl: false, webhookSecret: false, feishuAppSecret: false, slackBotToken: false } };
}

describe("notification editor credentials", () => {
  it("saves a newly typed credential only after explicit save", async () => {
    setup();
    await input("notifications.name").setValue("New notification");
    await input("settings.smtp_password").setValue("synthetic-first-secret");
    await input("settings.smtp_password").trigger("blur");
    expect(fixture.api).not.toHaveBeenCalled();
    await click("common.save");
    expect(fixture.api).toHaveBeenCalledWith("POST", "/api/notifications", expect.objectContaining({
      secrets: expect.objectContaining({ smtpPassword: { action: "replace", value: "synthetic-first-secret" } }),
    }), expect.any(AbortSignal));
    expect(wrapper.emitted("saved")).toHaveLength(1);
  });

  it("overwrites a saved credential without disclosing the previous value", async () => {
    setup(savedTask());
    expect((input("settings.smtp_password").element as HTMLInputElement).value).toBe("");
    expect((input("settings.smtp_password").element as HTMLInputElement).type).toBe("password");
    await input("settings.smtp_password").setValue("synthetic-replacement");
    await click("common.save");
    expect(fixture.api).toHaveBeenCalledWith("PUT", "/api/notifications/saved-task", expect.objectContaining({
      secrets: expect.objectContaining({ smtpPassword: { action: "replace", value: "synthetic-replacement" } }),
    }), expect.any(AbortSignal));
  });

  it("preserves the saved credential when a replacement is removed before saving", async () => {
    setup(savedTask());
    await input("settings.smtp_password").setValue("discarded-draft");
    await input("settings.smtp_password").setValue("");
    await input("notifications.name").setValue("Renamed notification");
    await click("common.save");
    expect(fixture.api).toHaveBeenCalledWith("PUT", "/api/notifications/saved-task", expect.objectContaining({
      name: "Renamed notification",
      secrets: expect.objectContaining({ smtpPassword: { action: "keep", value: "" } }),
    }), expect.any(AbortSignal));
  });

  it("cancels credential edits without persisting them", async () => {
    setup(savedTask());
    await input("settings.smtp_password").setValue("cancelled-secret");
    await click("common.cancel");
    expect(fixture.api).not.toHaveBeenCalled();
    expect(wrapper.emitted("close")).toHaveLength(1);
  });

  it("retains the typed credential after a failed save for an explicit retry", async () => {
    setup(savedTask());
    fixture.api.mockRejectedValueOnce(new Error("Controlled save failure"));
    await input("settings.smtp_password").setValue("retained-draft");
    await click("common.save");
    expect(wrapper.emitted("saved")).toBeUndefined();
    expect((input("settings.smtp_password").element as HTMLInputElement).value).toBe("retained-draft");
    await click("common.save");
    expect(fixture.api).toHaveBeenLastCalledWith("PUT", "/api/notifications/saved-task", expect.objectContaining({
      secrets: expect.objectContaining({ smtpPassword: { action: "replace", value: "retained-draft" } }),
    }), expect.any(AbortSignal));
    expect(wrapper.emitted("saved")).toHaveLength(1);
  });
});
