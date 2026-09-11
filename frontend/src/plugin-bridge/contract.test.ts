import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Frontend API 1.4 外部契约：精确版本匹配、host.* 能力面、18 个公开 slot 白名单、
 * renderer surface context 与清理、生命周期订阅与释放、dispose 后无残留注册。
 *
 * 宿主平台依赖经 host-adapter 注入，测试只替换该边界。
 */

const apiCalls: Array<{ method: string; path: string; body: unknown }> = [];
const toasts: Array<{ message: string; tone: string }> = [];
const previewCalls: Array<{ runId: string; plugin: string }> = [];
let apiResponder: (method: string, path: string, body: unknown) => unknown = () => null;

vi.mock("./host-adapter", () => ({
  api: async (method: string, path: string, body: unknown) => {
    apiCalls.push({ method, path, body });
    return apiResponder(method, path, body);
  },
  apiBlob: async () => new Blob(),
  isAbortError: () => false,
  formatCompactList: (values: unknown) => (Array.isArray(values) ? values.join("、") : String(values ?? "")),
  formatDate: (value: unknown) => String(value ?? ""),
  formatList: (values: unknown) => (Array.isArray(values) ? values.join("、") : String(values ?? "")),
  formatNumber: (value: unknown) => String(value ?? ""),
  formatTime: (value: unknown) => String(value ?? ""),
  getLocale: () => "zh-CN",
  t: (key: string, args: Record<string, unknown> = {}, fallback = "") => {
    const template = fallback || key;
    return Object.entries(args || {}).reduce(
      (result, [name, value]) => result.replaceAll(`{${name}}`, String(value ?? "")),
      template,
    );
  },
  clearFieldError: () => {},
  setRequiredFieldError: () => {},
  toast: (message: string, tone = "info") => { toasts.push({ message, tone }); },
  appearance: { init: async () => null },
  createAppearanceHost: (pluginName: string) => ({ pluginName }),
  derivePalette: async () => ({}),
  initAppearance: async () => null,
  refreshAppearance: async () => null,
  captureExecutionPreview: async (runId: string, plugin: string) => {
    previewCalls.push({ runId, plugin });
    return { state: "ready", url: null, source: "", capturedAt: null };
  },
  disposePage: () => {},
  enterPage: () => 1,
  registerInterval: (value: unknown) => value,
  releaseController: () => {},
  state: { page: "", routeToken: 0 },
  trackController: (value: unknown) => value,
}));

import {
  FRONTEND_API_VERSION,
  PLUGIN_SLOT_NAMES,
  createPluginHost,
  disposePluginSlot,
  notifyPluginDispose,
  notifyPluginPageEnter,
  notifyPluginPageLeave,
  notifyPluginPageUpdated,
  queryContributions,
  refreshPluginRuntime,
  renderFrontendSlots,
  resetPluginRuntimeForTests,
  resolvePluginRoute,
  syncPluginNavActive,
  verifyPluginApiVersion,
} from "./runtime";

const EXPECTED_SLOTS = [
  "dashboard.cards",
  "dashboard.after-running",
  "users.list.badges",
  "users.binding.sections",
  "users.global.sections",
  "scripts.list.badges",
  "scripts.editor.sections",
  "queues.list.badges",
  "queues.editor.sections",
  "dispatch.cards",
  "dispatch.running.badges",
  "dispatch.running.sidecar",
  "dispatch.run.sections",
  "history.list.badges",
  "history.detail.sections",
  "settings.sections",
  "settings.cards",
  "shell.nav",
];

let pluginSerial = 0;

function descriptorFixture() {
  pluginSerial += 1;
  return {
    name: `contract-fixture-${pluginSerial}`,
    displayName: "契约夹具",
    version: "1.0.0",
    frontendApiVersion: "1.4",
    entryUrl: `/plugins/contract-fixture-${pluginSerial}/web/main.js`,
    styleUrls: [],
    localization: { defaultLocale: "zh-CN", locales: { "zh-CN": { greeting: "占位文案 {name}" } } },
  };
}

type Host = any;

function installFixtureNav() {
  document.body.innerHTML = '<nav data-plugin-anchor="shell.nav"></nav>';
}

describe("Frontend API 1.4 contract", () => {
  beforeEach(() => {
    apiCalls.length = 0;
    toasts.length = 0;
    previewCalls.length = 0;
    apiResponder = () => null;
    document.body.innerHTML = "";
    resetPluginRuntimeForTests();
  });

  it("accepts only the exact 1.4 frontend API version", () => {
    expect(FRONTEND_API_VERSION).toBe("1.4");
    expect(verifyPluginApiVersion("1.4")).toBe(true);
    expect(verifyPluginApiVersion("1.3")).toBe(false);
    expect(verifyPluginApiVersion("1.5")).toBe(false);
    expect(verifyPluginApiVersion("1.4.0")).toBe(false);
    expect(verifyPluginApiVersion("")).toBe(false);
    expect(verifyPluginApiVersion(undefined)).toBe(false);
  });

  it("publishes exactly the 18 public slots", () => {
    expect([...PLUGIN_SLOT_NAMES]).toEqual(EXPECTED_SLOTS);
    expect(new Set(PLUGIN_SLOT_NAMES).size).toBe(18);
  });

  it("rejects querying an unknown slot instead of silently rendering it", async () => {
    await expect(queryContributions("unknown.slot", [], undefined)).rejects.toThrow();
    expect(apiCalls).toHaveLength(0);
  });

  it("queries contributions with the documented payload", async () => {
    apiResponder = () => ({ contributions: [] });
    await queryContributions("settings.cards", [{ mode: "user", primaryId: "u1", secondaryId: "s1" }], undefined);
    expect(apiCalls).toEqual([
      {
        method: "POST",
        path: "/api/plugin-contributions/ui/query",
        body: { slot: "settings.cards", contexts: [{ mode: "user", primaryId: "u1", secondaryId: "s1" }] },
      },
    ]);
  });

  it("exposes plugin slot surface context and runs renderer cleanup on dispose", async () => {
    const host = createPluginHost(descriptorFixture());
    const surfaces: Array<{ element: HTMLElement; context: Record<string, unknown> }> = [];
    const cleanup = vi.fn();
    host.slots.register("settings.cards", (surface: { element: HTMLElement; context: Record<string, unknown> }) => {
      surfaces.push(surface);
      return cleanup;
    });

    const container = document.createElement("div");
    document.body.append(container);
    const rendered = await renderFrontendSlots(container, "settings.cards", { mode: "detail", primaryId: "p1" });

    expect(rendered).toBe(1);
    expect(surfaces).toHaveLength(1);
    expect(surfaces[0].context).toEqual({ slot: "settings.cards", mode: "detail", primaryId: "p1" });
    expect(Object.isFrozen(surfaces[0].context)).toBe(true);
    expect(surfaces[0].element.className).toBe("plugin-surface");
    expect(surfaces[0].element.dataset.pluginSlot).toBe("settings.cards");
    expect(container.contains(surfaces[0].element)).toBe(true);

    await disposePluginSlot(container);
    expect(cleanup).toHaveBeenCalledTimes(1);

    // 同一容器再次渲染时不会复用已释放的 cleanup。
    await renderFrontendSlots(container, "settings.cards", { mode: "detail", primaryId: "p1" });
    expect(cleanup).toHaveBeenCalledTimes(1);
    await disposePluginSlot(container);
    expect(cleanup).toHaveBeenCalledTimes(2);
  });

  it("isolates renderer failures so one plugin cannot break the host slot", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const host = createPluginHost(descriptorFixture());
    host.slots.register("settings.cards", () => {
      throw new Error("renderer exploded");
    });
    const container = document.createElement("div");
    document.body.append(container);
    await expect(renderFrontendSlots(container, "settings.cards", {})).resolves.toBe(1);
    expect(warn).toHaveBeenCalled();
    warn.mockRestore();
  });

  it("accepts dispose-style renderer results as cleanup", async () => {
    const host = createPluginHost(descriptorFixture());
    const dispose = vi.fn();
    host.slots.register("settings.cards", () => ({ dispose }));
    const container = document.createElement("div");
    document.body.append(container);
    await renderFrontendSlots(container, "settings.cards", {});
    await disposePluginSlot(container);
    expect(dispose).toHaveBeenCalledTimes(1);
  });

  it("registers routes per plugin namespace and rejects duplicates", async () => {
    const descriptor = descriptorFixture();
    const host = createPluginHost(descriptor) as Host;
    const handler = vi.fn();
    const registration = host.routes.register("reports/daily", handler);
    expect(typeof registration.dispose).toBe("function");

    const segments = ["plugin", descriptor.name, "reports", "daily"];
    const resolved = resolvePluginRoute(segments);
    expect(typeof resolved).toBe("function");
    await resolved!(1, segments);
    expect(handler).toHaveBeenCalledWith(1, segments, undefined);

    expect(resolvePluginRoute(["plugin", "other-plugin", "reports", "daily"])).toBeNull();
    expect(resolvePluginRoute(["plugin", descriptor.name, "reports"])).toBeNull();
    expect(resolvePluginRoute(["plugin", descriptor.name])).toBeNull();

    expect(() => host.routes.register("reports/daily", handler)).toThrow();
    registration.dispose();
    expect(resolvePluginRoute(segments)).toBeNull();
  });

  it("rejects an empty or invalid route registration", () => {
    const host = createPluginHost(descriptorFixture()) as Host;
    expect(() => host.routes.register("", () => null)).toThrow();
    expect(() => host.routes.register("state", "not-a-function")).toThrow();
  });

  it("registers navigation items, syncs active state, and removes them on dispose", () => {
    installFixtureNav();
    const descriptor = descriptorFixture();
    const host = createPluginHost(descriptor) as Host;
    const nav = host.nav.register({ id: "reports", title: "报表", route: "reports", order: 2 });

    const link = document.querySelector<HTMLAnchorElement>("[data-plugin-nav]");
    expect(link).not.toBeNull();
    expect(link!.getAttribute("href")).toBe(`#/plugin/${descriptor.name}/reports`);
    expect(link!.textContent).toContain("报表");

    syncPluginNavActive(`#/plugin/${descriptor.name}/reports`);
    expect(link!.classList.contains("active")).toBe(true);
    expect(link!.getAttribute("aria-current")).toBe("page");
    syncPluginNavActive("#/dashboard");
    expect(link!.classList.contains("active")).toBe(false);
    expect(link!.hasAttribute("aria-current")).toBe(false);

    expect(() => host.nav.register({ id: "reports", title: "报表", route: "reports" })).toThrow();
    nav.dispose();
    expect(document.querySelector("[data-plugin-nav]")).toBeNull();
  });

  it("routes plugin api calls into the plugin namespace with all http verbs", async () => {
    apiResponder = () => ({ ok: true });
    const descriptor = descriptorFixture();
    const host = createPluginHost(descriptor) as Host;
    await host.api.get("state");
    await host.api.post("state", { value: 1 });
    await host.api.put("state", { value: 2 });
    await host.api.patch("state", { value: 3 });
    await host.api.delete("state", { value: 4 });
    expect(apiCalls.map(call => [call.method, call.path])).toEqual([
      ["GET", `/api/plugin-api/${descriptor.name}/state`],
      ["POST", `/api/plugin-api/${descriptor.name}/state`],
      ["PUT", `/api/plugin-api/${descriptor.name}/state`],
      ["PATCH", `/api/plugin-api/${descriptor.name}/state`],
      ["DELETE", `/api/plugin-api/${descriptor.name}/state`],
    ]);
    expect(apiCalls[1].body).toEqual({ value: 1 });
  });

  it("exposes ui.query/save/action/toast with the documented payloads", async () => {
    apiResponder = () => ({ contributions: [] });
    const descriptor = descriptorFixture();
    const host = createPluginHost(descriptor) as Host;
    await host.ui.query("settings.sections", [{ mode: "user", primaryId: "u1", secondaryId: "" }]);
    await host.ui.save(descriptor.name, "contribution-1", { mode: "user" }, { enabled: true });
    await host.ui.action(descriptor.name, "contribution-1", "reset", { mode: "user" }, {});
    host.ui.toast("已保存");
    host.ui.toast("失败", "error");

    expect(apiCalls).toEqual([
      {
        method: "POST",
        path: "/api/plugin-contributions/ui/query",
        body: { slot: "settings.sections", contexts: [{ mode: "user", primaryId: "u1", secondaryId: "" }] },
      },
      {
        method: "PUT",
        path: `/api/plugin-contributions/ui/${descriptor.name}/contribution-1`,
        body: { context: { mode: "user" }, values: { enabled: true } },
      },
      {
        method: "POST",
        path: `/api/plugin-contributions/ui/${descriptor.name}/contribution-1/action/reset`,
        body: { context: { mode: "user" }, values: {} },
      },
    ]);
    expect(toasts).toEqual([
      { message: "已保存", tone: "info" },
      { message: "失败", tone: "error" },
    ]);
  });

  it("exposes the plugin descriptor, localization, appearance and execution preview", async () => {
    const descriptor = descriptorFixture();
    const host = createPluginHost(descriptor) as Host;
    expect(host.plugin.name).toBe(descriptor.name);
    expect(host.plugin.displayName).toBe(descriptor.displayName);
    expect(host.plugin.frontendApiVersion).toBe("1.4");
    expect(Object.isFrozen(host.plugin)).toBe(true);

    expect(host.i18n.locale).toBe("zh-CN");
    expect(host.i18n.defaultLocale).toBe("zh-CN");
    expect(host.i18n.t("greeting", { name: "参数" })).toBe("占位文案 参数");
    expect(host.i18n.t("absent", {}, "回退")).toBe("回退");
    expect(typeof host.i18n.formatNumber).toBe("function");
    expect(typeof host.i18n.formatDate).toBe("function");
    expect(typeof host.i18n.formatTime).toBe("function");

    expect((host.appearance as { pluginName?: string }).pluginName).toBe(descriptor.name);
    await host.executionPreview.capture("run-1");
    expect(previewCalls).toEqual([{ runId: "run-1", plugin: descriptor.name }]);
  });

  it("delivers lifecycle notifications and stops after dispose", async () => {
    const host = createPluginHost(descriptorFixture()) as Host;
    const seen: string[] = [];
    const enter = host.lifecycle.onPageEnter((payload: { page: string }) => { seen.push(`enter:${payload.page}`); });
    const leave = host.lifecycle.onPageLeave((payload: { page: string }) => { seen.push(`leave:${payload.page}`); });
    const updated = host.lifecycle.onPageUpdated(() => { seen.push("updated"); });
    const disposed = host.lifecycle.onDispose(() => { seen.push("dispose"); });

    await notifyPluginPageEnter({ hash: "settings", page: "settings", segments: ["settings"] });
    await notifyPluginPageUpdated({ hash: "settings", page: "settings", segments: ["settings"] });
    await notifyPluginPageLeave({ hash: "settings", page: "settings", segments: ["settings"] });
    await notifyPluginDispose({ hash: "settings", page: "settings", segments: ["settings"] });
    expect(seen).toEqual(["enter:settings", "updated", "leave:settings", "dispose"]);

    enter.dispose(); leave.dispose(); updated.dispose(); disposed.dispose();
    seen.length = 0;
    await notifyPluginPageEnter({ hash: "settings", page: "settings", segments: ["settings"] });
    await notifyPluginDispose({ hash: "settings", page: "settings", segments: ["settings"] });
    expect(seen).toEqual([]);
  });

  it("rejects unknown lifecycle kinds instead of registering silently", () => {
    const host = createPluginHost(descriptorFixture()) as Host;
    expect(() => host.lifecycle.onPageEnter("not-a-function")).toThrow();
  });

  it("validates slot registration input", () => {
    const host = createPluginHost(descriptorFixture()) as Host;
    expect(() => host.slots.register("unknown.slot", () => {})).toThrow();
    expect(() => host.slots.register("dashboard.cards", "not-a-function")).toThrow();
  });

  it("rejects a duplicate renderer for the same plugin and slot", () => {
    const host = createPluginHost(descriptorFixture()) as Host;
    const renderer = () => () => {};
    host.slots.register("dashboard.cards", renderer);
    expect(() => host.slots.register("dashboard.cards", renderer)).toThrow();
  });

  it("keeps the runtime refresh path available to the host shell", async () => {
    apiResponder = () => [];
    await expect(refreshPluginRuntime()).resolves.toBe(true);
  });

  it("leaves no registration behind after dispose", async () => {
    installFixtureNav();
    const descriptor = descriptorFixture();
    const host = createPluginHost(descriptor) as Host;
    const slot = host.slots.register("settings.cards", () => () => {});
    const route = host.routes.register("cleanup", () => null);
    const nav = host.nav.register({ id: "cleanup", title: "清理", route: "cleanup" });
    const lifecycle = host.lifecycle.onPageEnter(() => {});

    slot.dispose(); route.dispose(); nav.dispose(); lifecycle.dispose();

    expect(document.querySelector("[data-plugin-nav]")).toBeNull();
    expect(resolvePluginRoute(["plugin", descriptor.name, "cleanup"])).toBeNull();
    const container = document.createElement("div");
    document.body.append(container);
    expect(await renderFrontendSlots(container, "settings.cards", {})).toBe(0);
  });
});
