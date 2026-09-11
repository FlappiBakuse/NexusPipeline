import { describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";

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
  return toggles[index].get(".badge");
}

describe("notification channel badges", () => {
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
