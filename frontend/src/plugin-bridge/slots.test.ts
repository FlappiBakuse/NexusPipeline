import { nextTick } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { apiMock, toastMock } = vi.hoisted(() => ({
  apiMock: vi.fn(),
  toastMock: vi.fn(),
}));

vi.mock("./host-adapter", () => ({
  api: apiMock,
  apiBlob: vi.fn(),
  apiUpload: vi.fn(),
  captureExecutionPreview: vi.fn(),
  clearFieldError: vi.fn(),
  createAppearanceHost: vi.fn(),
  getLocale: () => "zh-CN",
  isAbortError: () => false,
  setRequiredFieldError: vi.fn(),
  t: (key: string) => key,
  toast: toastMock,
}));

import { registerNexusElements } from "../ui/register";
import { renderPluginSlot } from "./slots";

async function settle() {
  await nextTick();
  await nextTick();
}

describe("declarative plugin slot rendering", () => {
  beforeEach(() => {
    registerNexusElements();
    document.body.innerHTML = "";
    apiMock.mockReset();
    toastMock.mockReset();
  });

  it("renders badges, cards and display fields through public elements", async () => {
    apiMock.mockResolvedValue({
      contributions: [
        {
          kind: "badge",
          pluginName: "demo",
          id: "state",
          values: { label: "Ready", tone: "ok", title: "Current state" },
        },
        {
          kind: "card",
          pluginName: "demo",
          id: "summary",
          title: "Summary",
          pluginDisplayName: "Demo",
          description: "A public card",
          values: {
            badges: [{ label: "Healthy", tone: "blue" }],
            fields: [{ label: "Owner", value: "Alice" }],
          },
        },
      ],
    });
    const container = document.createElement("div");
    document.body.append(container);

    await renderPluginSlot(container, "settings.cards", { mode: "detail" });
    await settle();

    const badge = container.querySelector<HTMLElement & { tone?: string }>("nxp-badge");
    expect(badge).toBeTruthy();
    expect(badge?.tone).toBe("ok");
    expect(badge?.getAttribute("title")).toBe("Current state");
    expect(badge?.textContent).toContain("Ready");

    const card = container.querySelector<HTMLElement>("nxp-card");
    expect(card).toBeTruthy();
    expect(card?.querySelector("h3")?.textContent).toBe("Summary");
    expect(card?.querySelector("nxp-badge")?.textContent).toContain("Healthy");
    expect(card?.querySelector("nxp-field.plugin-display-field .nxp-field-label")?.textContent).toBe("Owner");
    expect(card?.querySelector("nxp-field.plugin-display-field strong")?.textContent).toBe("Alice");
  });

  it("preserves field association, required validation, save payload and cleanup", async () => {
    let queryCount = 0;
    let savedBody: unknown = null;
    apiMock.mockImplementation(async (method: string, path: string, body: unknown) => {
      if (method === "POST" && path === "/api/plugin-contributions/ui/query") {
        queryCount += 1;
        return queryCount === 1
          ? {
              contributions: [{
                kind: "form",
                pluginName: "demo",
                id: "settings",
                context: { mode: "user", primaryId: "u1" },
                fields: [
                  { key: "name", type: "text", label: "Name", required: true },
                  { key: "enabled", type: "switch", label: "Enabled" },
                ],
                values: { name: "", enabled: false },
              }],
            }
          : { contributions: [] };
      }
      if (method === "PUT" && path === "/api/plugin-contributions/ui/demo/settings") {
        savedBody = body;
        return { ok: true };
      }
      return null;
    });
    const container = document.createElement("div");
    document.body.append(container);

    await renderPluginSlot(container, "settings.cards", { mode: "user", primaryId: "u1" });
    await settle();

    const form = container.querySelector<HTMLFormElement>("form.plugin-contribution-form");
    const field = container.querySelector<HTMLElement>("nxp-field.plugin-field");
    const name = container.querySelector<HTMLInputElement>("#plugin-demo-settings-name");
    expect(form).toBeTruthy();
    expect(field?.querySelector(".nxp-field-label")?.textContent).toContain("Name");
    expect(field?.querySelector(".nxp-field-label [aria-hidden='true']")).toBeTruthy();
    expect(field?.querySelector("label")?.getAttribute("for")).toBe("plugin-demo-settings-name");
    expect(name?.required).toBe(true);

    form?.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true }));
    expect(toastMock).toHaveBeenCalledWith("common.plugin.settings_required", "error");
    expect(savedBody).toBeNull();

    expect(name).toBeTruthy();
    if (name) {
      name.value = "Alice";
      name.dispatchEvent(new Event("change", { bubbles: true }));
    }
    await nextTick();
    form?.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(savedBody).toEqual({
      context: { mode: "user", primaryId: "u1" },
      values: { name: "Alice", enabled: false },
    }));

    const oldName = name;
    await renderPluginSlot(container, "settings.cards", { mode: "user", primaryId: "u1" });
    await settle();
    expect(container.querySelector("form.plugin-contribution-form")).toBeNull();
    expect(oldName?.isConnected).toBe(false);
  });
});
