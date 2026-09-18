import { beforeEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";

const { apiMock, toastMock } = vi.hoisted(() => ({
  apiMock: vi.fn(),
  toastMock: vi.fn(),
}));

vi.mock("../../../platform/api", () => ({
  api: apiMock,
  isAbortError: () => false,
}));

vi.mock("../../../platform/toast", () => ({
  toast: toastMock,
}));

import SettingsNotificationsSection from "./SettingsNotificationsSection.vue";

/**
 * 通知 section 的通道状态徽标：Webhook/SMTP 折叠面板头部按当前通道开关显示启用状态。
 * 徽标由宿主 `NxpBadge` 渲染；测试环境未加载语言资源，因此断言当前语言键与语义色。
 */
function mountSection(settings: Record<string, unknown>) {
  return mount(SettingsNotificationsSection, {
    props: {
      settings,
      expanded: true,
      save: vi.fn(),
      secretDraft: {},
    },
  });
}

function panelBadge(wrapper: ReturnType<typeof mountSection>, index: number) {
  const toggles = wrapper.findAll(".panel-toggle");
  expect(toggles.length).toBe(2);
  return toggles[index].get(".nxp-badge");
}

describe("notification channel badges", () => {
  beforeEach(() => {
    apiMock.mockReset();
    toastMock.mockReset();
  });

  it("keeps outer expand/collapse and webhook/smtp save actions intact", async () => {
    const settings = {
      webhookEnabled: true,
      smtpEnabled: false,
      webhookType: "generic",
      webhookUrl: "",
      smtpHost: "",
    };
    const secretDraft: Record<string, string> = {};
    const save = vi.fn();
    const wrapper = mount(SettingsNotificationsSection, {
      props: { settings, expanded: true, save, secretDraft },
    });

    const outerToggle = wrapper.get(".nxp-collapsible-card-toggle");
    expect(outerToggle.attributes("aria-expanded")).toBe("true");
    expect(outerToggle.attributes("aria-controls")).toBe("settings-panel-notifications");
    await outerToggle.trigger("click");
    expect(wrapper.emitted("toggle")).toEqual([[]]);
    await wrapper.setProps({ expanded: false });
    expect(outerToggle.attributes("aria-expanded")).toBe("false");

    const webhookUrl = wrapper.get("#st-whurl");
    await webhookUrl.setValue("https://hooks.example.test");
    await webhookUrl.trigger("blur");
    expect(secretDraft.webhookUrl).toBe("https://hooks.example.test");

    await wrapper.findAll(".panel-toggle")[1].trigger("click");
    const smtpHost = wrapper.get("#st-host");
    await smtpHost.setValue("smtp.example.test");
    await smtpHost.trigger("blur");

    expect(settings.smtpHost).toBe("smtp.example.test");
    expect(save).toHaveBeenCalledTimes(2);
  });

  it("sends the notification test through the existing API and toast contract", async () => {
    apiMock.mockResolvedValue({ ok: true });
    const wrapper = mountSection({ webhookEnabled: true, smtpEnabled: true });

    await wrapper.get(".modal-footer-inline .ghost").trigger("click");
    await vi.waitFor(() => expect(apiMock).toHaveBeenCalledWith("POST", "/api/settings/test"));
    expect(toastMock).toHaveBeenCalledWith("settings.notification.test_success", "info");
  });

  it("renders the enabled state for both channels", () => {
    const wrapper = mountSection({ webhookEnabled: true, smtpEnabled: true });

    expect(panelBadge(wrapper, 0).text()).toBe("common.enabled_status");
    expect(panelBadge(wrapper, 0).classes()).toContain("ok");
    expect(panelBadge(wrapper, 1).text()).toBe("common.enabled_status");
    expect(panelBadge(wrapper, 1).classes()).toContain("ok");
  });

  it("renders the disabled state for both channels", () => {
    const wrapper = mountSection({ webhookEnabled: false, smtpEnabled: false });

    expect(panelBadge(wrapper, 0).text()).toBe("common.disabled");
    expect(panelBadge(wrapper, 0).classes()).toContain("muted");
    expect(panelBadge(wrapper, 1).text()).toBe("common.disabled");
    expect(panelBadge(wrapper, 1).classes()).toContain("muted");
  });

  it("keeps the two channels independent", () => {
    const wrapper = mountSection({ webhookEnabled: true, smtpEnabled: false });

    expect(panelBadge(wrapper, 0).text()).toBe("common.enabled_status");
    expect(panelBadge(wrapper, 1).text()).toBe("common.disabled");
  });
});
