import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { mount, type VueWrapper } from "@vue/test-utils";
import { h, nextTick, ref } from "vue";
import { createMemoryHistory, createRouter, RouterView, useRoute, type Router } from "vue-router";

/**
 * 插件 route 的真实装配集成测试。
 *
 * 测试经真实 Vue Router 实例、`router.push()` 与 `RouterView` 组件装配路径驱动
 * `/plugin/:pathMatch(.*)*`，断言 `PluginRouteHost` 实际挂载并把当前 route segment
 * 交给 `initPluginRuntime` → `resolvePluginRoute` → 注册的 route handler → 页面生命周期。
 *
 * 只替换宿主平台在该边界上无法在 jsdom 中提供的能力：
 * - `@bridge/index` 是宿主消费插件能力的唯一 facade，这里记录其收到的真实调用；
 * - `plugin-bridge/host-adapter` 是桥接层访问宿主平台的唯一依赖边界，这里提供网络与外观替身。
 * 路由表、RouterView、`PluginRouteHost`、页面状态 `page-state` 与 shell 启动编排保持生产实现。
 */

/**
 * mock 工厂在模块初始化之前执行，因此调用记录只经全局句柄共享，
 * 工厂内不引用任何模块级绑定。
 */
interface BridgeCalls {
  initPluginRuntime: ReturnType<typeof vi.fn>;
  resolvePluginRoute: ReturnType<typeof vi.fn>;
  notifyPluginPageEnter: ReturnType<typeof vi.fn>;
  notifyPluginPageUpdated: ReturnType<typeof vi.fn>;
  notifyPluginPageLeave: ReturnType<typeof vi.fn>;
  notifyPluginDispose: ReturnType<typeof vi.fn>;
  syncPluginNavActive: ReturnType<typeof vi.fn>;
  disposePluginSlot: ReturnType<typeof vi.fn>;
  renderPluginSlot: ReturnType<typeof vi.fn>;
  pluginRuntimeStatus: ReturnType<typeof vi.fn>;
  refreshPluginRuntime: ReturnType<typeof vi.fn>;
}

const bridgeRegistry = globalThis as unknown as { __nxpBridgeCalls?: BridgeCalls };

bridgeRegistry.__nxpBridgeCalls ??= {
  initPluginRuntime: vi.fn(async () => true),
  resolvePluginRoute: vi.fn(),
  notifyPluginPageEnter: vi.fn(async () => {}),
  notifyPluginPageUpdated: vi.fn(async () => {}),
  notifyPluginPageLeave: vi.fn(async () => {}),
  notifyPluginDispose: vi.fn(async () => {}),
  syncPluginNavActive: vi.fn(),
  disposePluginSlot: vi.fn(async () => {}),
  renderPluginSlot: vi.fn(async () => 0),
  pluginRuntimeStatus: vi.fn(() => []),
  refreshPluginRuntime: vi.fn(async () => true),
};

const bridge = bridgeRegistry.__nxpBridgeCalls!;

vi.mock("@bridge/index", () => bridgeRegistry.__nxpBridgeCalls!);

vi.mock("./plugin-bridge/host-adapter", () => ({
  api: async () => null,
  apiBlob: async () => new Blob(),
  isAbortError: () => false,
  formatCompactList: (values: unknown) => (Array.isArray(values) ? values.join("、") : String(values ?? "")),
  formatDate: (value: unknown) => String(value ?? ""),
  formatList: (values: unknown) => (Array.isArray(values) ? values.join("、") : String(values ?? "")),
  formatNumber: (value: unknown) => String(value ?? ""),
  formatTime: (value: unknown) => String(value ?? ""),
  getLocale: () => "zh-CN",
  t: (key: string, args: Record<string, unknown> = {}, fallback = "") =>
    Object.entries(args || {}).reduce(
      (result, [name, value]) => result.replaceAll(`{${name}}`, String(value ?? "")),
      fallback || key,
    ),
  clearFieldError: () => {},
  setRequiredFieldError: () => {},
  toast: () => {},
  appearance: { init: async () => null },
  createAppearanceHost: (pluginName: string) => ({ pluginName }),
  derivePalette: async () => ({}),
  initAppearance: async () => null,
  refreshAppearance: async () => null,
  captureExecutionPreview: async () => ({ state: "ready", url: null, source: "", capturedAt: null }),
  disposePage: () => {},
  enterPage: () => 1,
  registerInterval: (value: unknown) => value,
  releaseController: () => {},
  state: { page: "", routeToken: 0 },
  trackController: (value: unknown) => value,
}));

import { routes } from "./router";
import { state } from "./platform/page-state";
import { registerNexusElements } from "./ui/register";

// 与 `main.ts` 一致：公开 `nxp-*` 元素按原生 Custom Element 注册。
registerNexusElements();

/** 启动编排含 `@bridge/index` 静态导入，必须在 mock 工厂注册之后加载。 */
const bootstrapModule = import("./app/bootstrap");

const PLUGIN_NAME = "example";
const LOADING_TESTID = "boot-loading";
const REGISTERED_ROUTE = "reports/daily";
const SECONDARY_ROUTE = "reports/weekly";
const PLUGIN_SEGMENTS = ["plugin", PLUGIN_NAME, "reports", "daily"];
const SECONDARY_SEGMENTS = ["plugin", PLUGIN_NAME, "reports", "weekly"];
const PLUGIN_ROUTE = { hash: `plugin/${PLUGIN_NAME}/reports/daily`, page: "plugin", segments: PLUGIN_SEGMENTS };

/** 已注册的插件 route；与插件侧 `host.routes.register()` 的 route 语义一致。 */
function isRegisteredPluginRoute(segments: unknown): boolean {
  if (!Array.isArray(segments) || segments.length < 3) return false;
  if (String(segments[0]).toLowerCase() !== "plugin" || String(segments[1]) !== PLUGIN_NAME) return false;
  return [REGISTERED_ROUTE, SECONDARY_ROUTE].includes(segments.slice(2).map(String).join("/"));
}

/**
 * 生产装配：`RouterView` 只在 shell 启动完成后渲染当前 route，
 * 并按 `route.fullPath` 建立页面代际（与 `App.vue` 相同）。
 */
function createRoutedShell(bootOutcome: { value: string }) {
  return {
    name: "RoutedShell",
    setup() {
      const route = useRoute();
      const booted = ref(false);
      void bootstrapModule
        .then(module => module.bootstrapFrontend())
        .then(result => {
          bootOutcome.value = result ? "booted" : "not-booted";
          if (result) booted.value = true;
        })
        .catch((error: unknown) => {
          bootOutcome.value = `boot-error: ${error instanceof Error ? error.message : String(error)}`;
        });
      return () => booted.value
        ? h(RouterView, { key: route.fullPath })
        : h("div", { "data-testid": LOADING_TESTID });
    },
  };
}

let router: Router;
let wrapper: VueWrapper | null = null;
let handler: ReturnType<typeof vi.fn>;

/** 推进渲染帧；动态路由组件与启动编排由模块加载器调度，需要真实宏任务时间片。 */
async function flush(rounds = 4) {
  for (let round = 0; round < rounds; round += 1) {
    await Promise.resolve();
    await nextTick();
    await new Promise(resolve => setTimeout(resolve, 0));
  }
}

/** 动态导入与异步生命周期通知的实际完成时刻由条件决定，而非固定轮数。 */
async function until(condition: () => boolean, message: string) {
  const deadline = Date.now() + 5000;
  while (Date.now() < deadline) {
    if (condition()) return;
    await flush(1);
  }
  throw new Error(`等待条件超时：${message}`);
}

async function createHarness() {
  router = createRouter({ history: createMemoryHistory(), routes });
  const pinia = createPinia();
  setActivePinia(pinia);
  const host = document.createElement("div");
  host.id = "integration-root";
  document.body.append(host);
  const bootOutcome = { value: "pending" };
  wrapper = mount(createRoutedShell(bootOutcome), {
    attachTo: host,
    global: { plugins: [router, pinia] },
  });
  await until(() => bootOutcome.value !== "pending", `shell 启动完成（${bootOutcome.value}）`);
  if (bootOutcome.value !== "booted") throw new Error(`shell 启动失败：${bootOutcome.value}`);
  await flush();
  return router;
}

function payloads(spy: { mock: { calls: unknown[][] } }) {
  return spy.mock.calls.map(call => call[0] as Record<string, unknown>);
}

function resolvedSegments() {
  return bridge.resolvePluginRoute.mock.calls.map(call => call[0] as string[]);
}

function clearLifecycle() {
  bridge.notifyPluginPageLeave.mockClear();
  bridge.notifyPluginDispose.mockClear();
  bridge.notifyPluginPageEnter.mockClear();
  bridge.notifyPluginPageUpdated.mockClear();
}

beforeEach(() => {
  for (const spy of Object.values(bridge)) spy.mockClear();
  bridge.resolvePluginRoute.mockReset();
  handler = vi.fn(async () => {});
  bridge.resolvePluginRoute.mockImplementation((segments: string[]) => (
    isRegisteredPluginRoute(segments) ? handler : null
  ));
  document.body.innerHTML = [
    '<div class="page-shell"></div>',
    '<nav data-plugin-anchor="shell.nav">',
    `<a data-plugin-nav href="#/plugin/${PLUGIN_NAME}/reports">报表</a>`,
    "</nav>",
  ].join("");
  vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } })));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
  document.body.innerHTML = "";
  vi.unstubAllGlobals();
});

describe("plugin route integration through the real router", () => {
  it("mounts PluginRouteHost and dispatches the registered handler with the route segments", async () => {
    await createHarness();
    await router.push(`/plugin/${PLUGIN_NAME}/reports/daily`);
    await until(() => handler.mock.calls.length === 1, "route handler 执行");

    expect(wrapper!.findComponent({ name: "PluginRouteHost" }).exists()).toBe(true);
    expect(bridge.initPluginRuntime).toHaveBeenCalled();
    expect(resolvedSegments()).toContainEqual(PLUGIN_SEGMENTS);

    const [token, segments] = handler.mock.calls[0] as [number, string[]];
    expect(typeof token).toBe("number");
    expect(token).toBe(state.routeToken);
    expect(segments).toEqual(PLUGIN_SEGMENTS);

    const entered = payloads(bridge.notifyPluginPageEnter);
    const updated = payloads(bridge.notifyPluginPageUpdated);
    expect(entered).toHaveLength(1);
    expect(updated).toHaveLength(1);
    expect(entered[0]).toMatchObject({ ...PLUGIN_ROUTE, token });
    expect(updated[0]).toMatchObject({ ...PLUGIN_ROUTE, token });
    expect(entered[0].container).toBe(document.querySelector("#view"));
  });

  it("enters a plugin route directly after boot without visiting a host page first", async () => {
    await createHarness();
    await router.push(`/plugin/${PLUGIN_NAME}/reports/daily`);
    await until(() => handler.mock.calls.length === 1, "直接进入插件 route");

    expect(wrapper!.findComponent({ name: "PluginRouteHost" }).exists()).toBe(true);
    expect(resolvedSegments()).toEqual([PLUGIN_SEGMENTS]);
    expect(payloads(bridge.notifyPluginPageEnter)[0].segments).toEqual(PLUGIN_SEGMENTS);
  });

  it("leaves and disposes the page once when a plugin route hands over to a host route", async () => {
    await createHarness();
    await router.push(`/plugin/${PLUGIN_NAME}/reports/daily`);
    await until(() => handler.mock.calls.length === 1, "插件页面进入");
    clearLifecycle();

    await router.push("/settings");
    await until(() => bridge.notifyPluginPageLeave.mock.calls.length === 1, "插件页面离开");

    expect(wrapper!.findComponent({ name: "PluginRouteHost" }).exists()).toBe(false);
    expect(bridge.notifyPluginDispose).toHaveBeenCalledTimes(1);
    expect(bridge.notifyPluginPageEnter).not.toHaveBeenCalled();
    expect(bridge.notifyPluginPageUpdated).not.toHaveBeenCalled();
    expect(payloads(bridge.notifyPluginPageLeave)[0]).toMatchObject(PLUGIN_ROUTE);
    expect(state.page).toBe("");
  });

  it("handles a plugin to plugin handover with one leave, one dispose and a fresh token", async () => {
    await createHarness();
    await router.push(`/plugin/${PLUGIN_NAME}/reports/daily`);
    await until(() => handler.mock.calls.length === 1, "前一个插件页面进入");
    const firstToken = (handler.mock.calls[0] as [number, string[]])[0];
    clearLifecycle();

    await router.push(`/plugin/${PLUGIN_NAME}/reports/weekly`);
    await until(() => handler.mock.calls.length === 2, "新插件页面进入");

    const secondToken = (handler.mock.calls[1] as [number, string[]])[0];
    expect(secondToken).not.toBe(firstToken);
    expect(resolvedSegments().at(-1)).toEqual(SECONDARY_SEGMENTS);
    expect(bridge.notifyPluginPageLeave).toHaveBeenCalledTimes(1);
    expect(bridge.notifyPluginDispose).toHaveBeenCalledTimes(1);
    expect(bridge.notifyPluginPageEnter).toHaveBeenCalledTimes(1);
    expect(bridge.notifyPluginPageUpdated).toHaveBeenCalledTimes(1);
    expect(payloads(bridge.notifyPluginPageLeave)[0]).toMatchObject(PLUGIN_ROUTE);
    expect(payloads(bridge.notifyPluginPageEnter)[0]).toMatchObject({
      hash: `plugin/${PLUGIN_NAME}/reports/weekly`,
      page: "plugin",
      segments: SECONDARY_SEGMENTS,
    });
  });

  it("falls back to the dashboard for an unregistered plugin route", async () => {
    await createHarness();
    await router.push("/plugin/unknown/missing");
    await until(() => window.location.hash === "#/dashboard", "回退到 Dashboard");

    // 无效插件 route 不执行 handler、不进入页面生命周期，并请求回到 Dashboard。
    expect(handler).not.toHaveBeenCalled();
    expect(bridge.notifyPluginPageEnter).not.toHaveBeenCalled();
    expect(bridge.notifyPluginPageUpdated).not.toHaveBeenCalled();
    expect(bridge.syncPluginNavActive).not.toHaveBeenCalled();
  });

  it("syncs the plugin nav active state from the current location", async () => {
    await createHarness();
    await router.push(`/plugin/${PLUGIN_NAME}/reports/daily`);
    await until(() => bridge.syncPluginNavActive.mock.calls.length === 1, "插件导航同步");

    expect(bridge.syncPluginNavActive).toHaveBeenCalledWith(window.location.hash);
  });

  it("keeps the v0.15.7 fullPath generation semantics for query changes", async () => {
    await createHarness();
    await router.push(`/plugin/${PLUGIN_NAME}/reports/daily`);
    await until(() => handler.mock.calls.length === 1, "首次进入插件页面");
    const firstToken = (handler.mock.calls[0] as [number, string[]])[0];
    clearLifecycle();

    await router.push(`/plugin/${PLUGIN_NAME}/reports/daily?tab=hourly`);
    await until(() => handler.mock.calls.length === 2, "query 变化后的页面代际");

    // fullPath 代际语义：query 变化重新挂载页面，旧代际离开并释放，新代际重新进入。
    const secondToken = (handler.mock.calls[1] as [number, string[]])[0];
    expect(secondToken).not.toBe(firstToken);
    expect(bridge.notifyPluginPageLeave).toHaveBeenCalledTimes(1);
    expect(bridge.notifyPluginDispose).toHaveBeenCalledTimes(1);
    expect(bridge.notifyPluginPageEnter).toHaveBeenCalledTimes(1);
    expect(bridge.notifyPluginPageUpdated).toHaveBeenCalledTimes(1);
    expect(resolvedSegments().at(-1)).toEqual(PLUGIN_SEGMENTS);
  });

  it("never renders the plugin host for host routes", async () => {
    await createHarness();
    await router.push("/dashboard");

    expect(wrapper!.findComponent({ name: "PluginRouteHost" }).exists()).toBe(false);
    expect(bridge.resolvePluginRoute).not.toHaveBeenCalled();
    expect(handler).not.toHaveBeenCalled();
  });
});
