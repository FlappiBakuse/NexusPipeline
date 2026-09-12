// @ts-nocheck
/**
 * Frontend API 1.5 插件运行时：同源模块加载、route/nav/slot/lifecycle 注册、
 * 插件 Web API（JSON 与二进制）、本地化、外观与运行预览宿主访问。
 *
 * 宿主平台依赖只通过 host-adapter 获取。
 */
import {
  api,
  apiBlob,
  apiUpload,
  captureExecutionPreview,
  createAppearanceHost,
  getLocale,
  t,
  toast,
} from "./host-adapter";

export const PLUGIN_SLOT_NAMES = Object.freeze([
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
]);

const SLOT_NAMES = new Set(PLUGIN_SLOT_NAMES);

/** Frontend API 的精确版本；只有完全匹配的插件模块才会被加载。 */
export const FRONTEND_API_VERSION = "1.5";

/** 插件可自行校验宿主 Frontend API 版本；非 1.5 一律返回 false。 */
export function verifyPluginApiVersion(value) {
  return String(value ?? "") === FRONTEND_API_VERSION;
}

const plugins = new Map();
const routes = new Map();
const navItems = new Map();
const slotRenderers = new Map();
const lifecycle = new Map([
  ["onPageEnter", []],
  ["onPageLeave", []],
  ["onPageUpdated", []],
  ["onDispose", []],
]);
const slotCleanups = new WeakMap();
let runtimePromise = null;

function disposable(dispose) {
  let done = false;
  return {
    dispose() {
      if (done) return;
      done = true;
      dispose?.();
    },
  };
}

function normalizeRoute(route) {
  return String(route || "").trim().replace(/^\/+|\/+$/g, "");
}

function pluginKey(name, value) {
  return `${String(name || "").toLowerCase()}:${value}`;
}

function pluginApiPath(name, route, query) {
  const parts = normalizeRoute(route)
    .split("/")
    .filter(Boolean)
    .map(part => encodeURIComponent(part));
  const search = new URLSearchParams();
  Object.entries(query || {}).forEach(([key, value]) => {
    if (value === undefined || value === null || value === "") return;
    search.append(key, String(value));
  });
  const suffix = search.toString();
  return `/api/plugin-api/${encodeURIComponent(name)}${parts.length ? `/${parts.join("/")}` : ""}${suffix ? `?${suffix}` : ""}`;
}

function registerRoute(descriptor, route, handler) {
  const normalized = normalizeRoute(route);
  if (!normalized || typeof handler !== "function") throw new TypeError(t("common.plugin_route_invalid"));
  const key = pluginKey(descriptor.name, normalized);
  if (routes.has(key)) throw new Error(t("common.plugin_route_duplicate", { route: normalized }));
  routes.set(key, { handler, plugin: descriptor.name, route: normalized });
  return disposable(() => routes.delete(key));
}

function renderPluginNav() {
  const navs = document.querySelectorAll('[data-plugin-slot="shell.nav"], [data-plugin-anchor="shell.nav"]');
  navs.forEach(nav => {
    nav.querySelectorAll("[data-plugin-nav]").forEach(item => item.remove());
    const items = Array.from(navItems.values())
      .sort((left, right) => (left.order - right.order) || left.title.localeCompare(right.title, "zh-CN"));
    items.forEach(item => {
      const link = document.createElement("a");
      link.href = item.href;
      link.className = "plugin-nav-item";
      link.dataset.pluginNav = item.key;
      link.dataset.pluginPage = item.href;
      const icon = document.createElement("span");
      icon.className = "nav-icon plugin-nav-icon";
      icon.setAttribute("aria-hidden", "true");
      icon.textContent = item.icon || "•";
      const title = document.createElement("span");
      title.textContent = item.title;
      link.append(icon, title);
      nav.append(link);
      item.element = link;
    });
  });
  syncPluginNavActive(location.hash);
}

function registerNav(descriptor, item = {}) {
  const id = String(item.id || item.route || "item").trim();
  const title = String(item.title || "").trim();
  const route = normalizeRoute(item.route || id);
  if (!title || !route || !/^[^#?]+$/.test(route)) throw new TypeError(t("common.plugin_navigation_invalid"));
  const key = pluginKey(descriptor.name, id);
  if (navItems.has(key)) throw new Error(t("common.plugin_navigation_duplicate", { id }));
  const value = {
    key,
    title,
    order: Number.isFinite(Number(item.order)) ? Number(item.order) : 0,
    icon: String(item.icon || "•").slice(0, 2),
    href: `#/plugin/${encodeURIComponent(descriptor.name)}/${route.split("/").map(encodeURIComponent).join("/")}`,
    element: null,
  };
  navItems.set(key, value);
  renderPluginNav();
  return disposable(() => {
    navItems.delete(key);
    renderPluginNav();
  });
}

function registerSlot(descriptor, slot, renderer) {
  if (!SLOT_NAMES.has(slot) || typeof renderer !== "function") throw new TypeError(t("common.plugin_slot_renderer_invalid"));
  const list = slotRenderers.get(slot) || [];
  if (list.some(item => item.plugin === descriptor.name && item.renderer === renderer)) {
    throw new Error(t("common.plugin_slot_renderer_duplicate", { slot }));
  }
  const registration = { plugin: descriptor.name, renderer, host: null };
  list.push(registration);
  slotRenderers.set(slot, list);
  return disposable(() => {
    const current = slotRenderers.get(slot) || [];
    const index = current.indexOf(registration);
    if (index >= 0) current.splice(index, 1);
    if (!current.length) slotRenderers.delete(slot);
  });
}

function registerLifecycle(kind, handler) {
  if (!lifecycle.has(kind) || typeof handler !== "function") throw new TypeError(t("common.plugin_lifecycle_handler_invalid"));
  const list = lifecycle.get(kind);
  list.push(handler);
  return disposable(() => {
    const index = list.indexOf(handler);
    if (index >= 0) list.splice(index, 1);
  });
}

function createPluginI18n(descriptor) {
  const localization = descriptor.localization || {};
  const defaultLocale = localization.defaultLocale || "zh-CN";
  const resources = localization.locales || {};
  const normalize = value => {
    const raw = String(value || "").trim().toLowerCase();
    if (raw === "en" || raw.startsWith("en-")) return "en-US";
    return "zh-CN";
  };
  const format = (value, args) => Object.entries(args || {}).reduce(
    (result, [key, replacement]) => result.replaceAll(`{${key}}`, String(replacement ?? "")),
    String(value ?? ""));
  const lookup = (locale, key) => resources[locale]?.[key] ?? resources[defaultLocale]?.[key];
  return Object.freeze({
    locale: getLocale(),
    defaultLocale,
    t: (key, args = {}, fallback = "") => format(lookup(normalize(getLocale()), key) ?? fallback ?? key, args),
    formatNumber: (value, options) => {
      try { return new Intl.NumberFormat(getLocale(), options).format(value); } catch { return String(value ?? ""); }
    },
    formatDate: (value, options) => {
      try { return new Intl.DateTimeFormat(getLocale(), options).format(new Date(value)); } catch { return String(value ?? ""); }
    },
    formatTime: (value, options) => {
      try { return new Intl.DateTimeFormat(getLocale(), { timeStyle: "short", ...options }).format(new Date(value)); } catch { return String(value ?? ""); }
    },
  });
}

/** 构造授予插件的 host 对象；`activateDescriptor` 与 contract tests 都经此入口。 */
export function createPluginHost(descriptor) {  const host = {
    plugin: Object.freeze({ ...descriptor }),
    i18n: createPluginI18n(descriptor),
    api: {
      get: (route, signal) => api("GET", pluginApiPath(descriptor.name, route), undefined, signal),
      post: (route, body, signal) => api("POST", pluginApiPath(descriptor.name, route), body, signal),
      put: (route, body, signal) => api("PUT", pluginApiPath(descriptor.name, route), body, signal),
      patch: (route, body, signal) => api("PATCH", pluginApiPath(descriptor.name, route), body, signal),
      delete: (route, body, signal) => api("DELETE", pluginApiPath(descriptor.name, route), body, signal),
      blob: (route, options = {}) => apiBlob(pluginApiPath(descriptor.name, route, options.query), options.signal),
      upload: (route, body, options = {}) => apiUpload(
        options.method || "POST",
        pluginApiPath(descriptor.name, route, options.query),
        body,
        options.contentType || (body && body.type) || "application/octet-stream",
        options.signal),
    },
    routes: {
      register: (route, handler) => registerRoute(descriptor, route, handler),
    },
    nav: {
      register: item => registerNav(descriptor, item),
    },
    slots: {
      register: (slot, renderer) => {
        const result = registerSlot(descriptor, slot, renderer);
        const registration = (slotRenderers.get(slot) || []).find(item => item.plugin === descriptor.name && item.renderer === renderer);
        if (registration) registration.host = host;
        return result;
      },
    },
    ui: {
      query: (slot, contexts = [{ mode: "", primaryId: "", secondaryId: "" }], signal) =>
        queryContributions(slot, contexts, signal),
      save: (pluginName, contributionId, context, values, signal) =>
        api("PUT", `/api/plugin-contributions/ui/${encodeURIComponent(pluginName)}/${encodeURIComponent(contributionId)}`, { context, values }, signal),
      action: (pluginName, contributionId, action, context, values = {}, signal) =>
        api("POST", `/api/plugin-contributions/ui/${encodeURIComponent(pluginName)}/${encodeURIComponent(contributionId)}/action/${encodeURIComponent(action)}`, { context, values }, signal),
      toast: (message, tone = "info") => toast(message, tone),
    },
    executionPreview: {
      capture: (runId, signal) => captureExecutionPreview(runId, descriptor.name, signal),
    },
    lifecycle: {
      onPageEnter: handler => registerLifecycle("onPageEnter", handler),
      onPageLeave: handler => registerLifecycle("onPageLeave", handler),
      onPageUpdated: handler => registerLifecycle("onPageUpdated", handler),
      onDispose: handler => registerLifecycle("onDispose", handler),
    },
    appearance: createAppearanceHost(),
  };
  return host;
}

async function activateDescriptor(descriptor) {
  if (!descriptor?.name || plugins.has(String(descriptor.name).toLowerCase())) return;
  try {
    (descriptor.styleUrls || []).forEach((url, index) => {
      if (typeof url !== "string" || !url.endsWith(".css")) return;
      const link = document.createElement("link");
      link.rel = "stylesheet";
      link.href = url;
      link.dataset.pluginStyle = `${descriptor.name}-${index}`;
      document.head.append(link);
    });
    const module = await import(descriptor.entryUrl);
    if (typeof module.activate !== "function") throw new Error(t("common.plugin_activate_missing"));
    const host = createPluginHost(descriptor);
    const result = await module.activate(host);
    plugins.set(String(descriptor.name).toLowerCase(), {
      descriptor,
      host,
      dispose: typeof result === "function" ? result : result?.dispose || result?.deactivate,
    });
  } catch (error) {
    console.warn(`[NexusPipeline] ${t("common.plugin_frontend_load_failed", { name: descriptor.name })}`, error);
  }
}

export async function initPluginRuntime() {
  if (runtimePromise) return runtimePromise;
  runtimePromise = (async () => {
    try {
      const payload = await api("GET", "/api/plugin-runtime/frontend");
      const descriptors = Array.isArray(payload) ? payload : (payload?.plugins || []);
      for (const descriptor of descriptors) await activateDescriptor(descriptor);
      renderPluginNav();
      return true;
    } catch (error) {
      console.warn("[NexusPipeline]", t("common.plugin_frontend_not_started"), error);
      return false;
    }
  })();
  return runtimePromise;
}

export async function refreshPluginRuntime() {
  await initPluginRuntime();
  try {
    const payload = await api("GET", "/api/plugin-runtime/frontend");
    const descriptors = Array.isArray(payload) ? payload : (payload?.plugins || []);
    for (const descriptor of descriptors) await activateDescriptor(descriptor);
    renderPluginNav();
    return true;
  } catch {
    return false;
  }
}

export function resolvePluginRoute(segments) {
  if (!Array.isArray(segments) || segments.length < 3 || String(segments[0]).toLowerCase() !== "plugin") return null;
  let pluginName;
  let route;
  try {
    pluginName = decodeURIComponent(segments[1]);
    route = segments.slice(2).map(segment => decodeURIComponent(segment)).join("/");
  } catch {
    return null;
  }
  const registration = routes.get(pluginKey(pluginName, normalizeRoute(route)));
  if (!registration) return null;
  return (token, routeSegments) => registration.handler(token, routeSegments, registration.host || plugins.get(pluginName.toLowerCase())?.host);
}

export async function queryContributions(slot, contexts, signal) {
  if (!SLOT_NAMES.has(slot)) throw new Error(t("common.plugin_slot_unsupported", { slot }));
  return api("POST", "/api/plugin-contributions/ui/query", { slot, contexts: contexts || [] }, signal);
}

export async function renderFrontendSlots(container, slot, context = {}) {
  if (!container || !SLOT_NAMES.has(slot)) return 0;
  const oldCleanups = slotCleanups.get(container) || [];
  oldCleanups.splice(0).forEach(cleanup => {
    try { cleanup(); } catch (error) { console.warn("[NexusPipeline]", t("common.plugin_slot_cleanup_failed"), error); }
  });
  const cleanups = [];
  const registrations = slotRenderers.get(slot) || [];
  for (const registration of registrations.slice()) {
    try {
      const element = document.createElement("div");
      element.className = "plugin-surface";
      element.dataset.pluginName = registration.plugin;
      element.dataset.pluginSlot = slot;
      container.append(element);
      const surface = {
        element,
        context: Object.freeze({ slot, ...context }),
      };
      const result = await registration.renderer(surface);
      if (typeof result === "function") cleanups.push(result);
      else if (result && typeof result.dispose === "function") cleanups.push(() => result.dispose());
      else if (result && typeof result.deactivate === "function") cleanups.push(() => result.deactivate());
    } catch (error) {
      console.warn(`[NexusPipeline] ${t("common.plugin_slot_render_failed", { plugin: registration.plugin, slot })}`, error);
    }
  }
  slotCleanups.set(container, cleanups);
  return registrations.length;
}

export async function disposePluginSlot(container) {
  const cleanups = slotCleanups.get(container) || [];
  slotCleanups.delete(container);
  cleanups.splice(0).forEach(cleanup => {
    try { cleanup(); } catch (error) { console.warn("[NexusPipeline]", t("common.plugin_slot_cleanup_failed"), error); }
  });
}

async function notifyLifecycle(kind, payload) {
  const handlers = lifecycle.get(kind) || [];
  for (const handler of handlers.slice()) {
    try { await handler(payload); } catch (error) { console.warn("[NexusPipeline]", t("common.plugin_lifecycle_failed", { kind }), error); }
  }
}

export function notifyPluginPageEnter(payload) {
  return notifyLifecycle("onPageEnter", payload);
}

export function notifyPluginPageLeave(payload) {
  return notifyLifecycle("onPageLeave", payload);
}

export function notifyPluginPageUpdated(payload) {
  return notifyLifecycle("onPageUpdated", payload);
}

export function notifyPluginDispose(payload) {
  return notifyLifecycle("onDispose", payload);
}

export function syncPluginNavActive(hash = location.hash) {
  document.querySelectorAll("[data-plugin-nav]").forEach(link => {
    const active = link.getAttribute("href") === hash;
    link.classList.toggle("active", active);
    if (active) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  });
}

export function pluginRuntimeStatus() {
  return Array.from(plugins.values()).map(item => ({ ...item.descriptor }));
}

/** 清空全部插件注册状态；供宿主 contract tests 在用例之间建立干净边界。 */
export function resetPluginRuntimeForTests() {
  plugins.clear();
  routes.clear();
  navItems.clear();
  slotRenderers.clear();
  lifecycle.forEach(list => { list.length = 0; });
  renderPluginNav();
  runtimePromise = null;
}
