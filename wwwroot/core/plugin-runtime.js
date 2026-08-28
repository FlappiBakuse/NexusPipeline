import { api } from "./api.js";
import { appearance, createAppearanceHost, refreshAppearance } from "./appearance.js";

const SLOT_NAMES = new Set([
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
  "dispatch.run.sections",
  "history.list.badges",
  "history.detail.sections",
  "settings.sections",
  "settings.cards",
  "shell.nav",
]);

const plugins = new Map();
const actions = new Map();
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

function normalizeActionKey(value) {
  return String(value || "").trim().toLowerCase();
}

function pluginKey(name, value) {
  return `${String(name || "").toLowerCase()}:${value}`;
}

function pluginApiPath(name, route) {
  const parts = normalizeRoute(route)
    .split("/")
    .filter(Boolean)
    .map(part => encodeURIComponent(part));
  return `/api/plugin-api/${encodeURIComponent(name)}${parts.length ? `/${parts.join("/")}` : ""}`;
}

function registerAction(descriptor, id, handler) {
  if (!id || typeof handler !== "function") throw new TypeError("插件 action 无效");
  const actionId = `plugin:${descriptor.name}:${String(id).trim()}`;
  const key = normalizeActionKey(actionId);
  if (actions.has(key)) throw new Error(`插件 action 重复：${actionId}`);
  actions.set(key, { handler, plugin: descriptor.name });
  return disposable(() => actions.delete(key));
}

function registerRoute(descriptor, route, handler) {
  const normalized = normalizeRoute(route);
  if (!normalized || typeof handler !== "function") throw new TypeError("插件 route 无效");
  const key = pluginKey(descriptor.name, normalized);
  if (routes.has(key)) throw new Error(`插件 route 重复：${normalized}`);
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
  if (!title || !route || !/^[^#?]+$/.test(route)) throw new TypeError("插件导航项无效");
  const key = pluginKey(descriptor.name, id);
  if (navItems.has(key)) throw new Error(`插件导航项重复：${id}`);
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
  if (!SLOT_NAMES.has(slot) || typeof renderer !== "function") throw new TypeError("插件 UI slot renderer 无效");
  const list = slotRenderers.get(slot) || [];
  if (list.some(item => item.plugin === descriptor.name && item.renderer === renderer)) {
    throw new Error(`插件 slot renderer 重复：${slot}`);
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
  if (!lifecycle.has(kind) || typeof handler !== "function") throw new TypeError("插件生命周期处理器无效");
  const list = lifecycle.get(kind);
  list.push(handler);
  return disposable(() => {
    const index = list.indexOf(handler);
    if (index >= 0) list.splice(index, 1);
  });
}

function createHost(descriptor) {
  const host = {
    plugin: Object.freeze({ ...descriptor }),
    api: {
      get: (route, signal) => api("GET", pluginApiPath(descriptor.name, route), undefined, signal),
      post: (route, body, signal) => api("POST", pluginApiPath(descriptor.name, route), body, signal),
      put: (route, body, signal) => api("PUT", pluginApiPath(descriptor.name, route), body, signal),
      patch: (route, body, signal) => api("PATCH", pluginApiPath(descriptor.name, route), body, signal),
      delete: (route, body, signal) => api("DELETE", pluginApiPath(descriptor.name, route), body, signal),
    },
    actions: {
      register: (id, handler) => registerAction(descriptor, id, handler),
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
    },
    lifecycle: {
      onPageEnter: handler => registerLifecycle("onPageEnter", handler),
      onPageLeave: handler => registerLifecycle("onPageLeave", handler),
      onPageUpdated: handler => registerLifecycle("onPageUpdated", handler),
      onDispose: handler => registerLifecycle("onDispose", handler),
    },
    appearance: createAppearanceHost(descriptor.name),
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
    if (typeof module.activate !== "function") throw new Error("缺少 activate 导出");
    const host = createHost(descriptor);
    const result = await module.activate(host);
    plugins.set(String(descriptor.name).toLowerCase(), {
      descriptor,
      host,
      dispose: typeof result === "function" ? result : result?.dispose || result?.deactivate,
    });
  } catch (error) {
    console.warn(`[NexusPipeline] 插件前端加载失败：${descriptor.name}`, error);
  }
}

export async function initPluginRuntime() {
  if (runtimePromise) return runtimePromise;
  runtimePromise = (async () => {
    try {
      const payload = await api("GET", "/api/plugin-runtime/frontend");
      const descriptors = Array.isArray(payload) ? payload : (payload?.plugins || []);
      for (const descriptor of descriptors) await activateDescriptor(descriptor);
      await appearance.init();
      renderPluginNav();
      return true;
    } catch (error) {
      console.warn("[NexusPipeline] 插件前端运行时未启动", error);
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
    await refreshAppearance();
    renderPluginNav();
    return true;
  } catch {
    return false;
  }
}

export function resolvePluginAction(actionName) {
  return actions.get(normalizeActionKey(actionName))?.handler || null;
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

export async function queryContributions(slot, contexts = [], signal) {
  if (!SLOT_NAMES.has(slot)) throw new Error(`插件 UI slot 不受支持：${slot}`);
  return api("POST", "/api/plugin-contributions/ui/query", { slot, contexts }, signal);
}

export async function renderFrontendSlots(container, slot, context = {}) {
  if (!container || !SLOT_NAMES.has(slot)) return 0;
  const oldCleanups = slotCleanups.get(container) || [];
  oldCleanups.splice(0).forEach(cleanup => {
    try { cleanup(); } catch (error) { console.warn("[NexusPipeline] 插件 slot 清理失败", error); }
  });
  const cleanups = [];
  const registrations = slotRenderers.get(slot) || [];
  for (const registration of registrations.slice()) {
    try {
      const result = await registration.renderer(container, { slot, ...context }, registration.host || plugins.get(registration.plugin)?.host);
      if (typeof result === "function") cleanups.push(result);
    } catch (error) {
      console.warn(`[NexusPipeline] 插件 slot 渲染失败：${registration.plugin}/${slot}`, error);
    }
  }
  slotCleanups.set(container, cleanups);
  return registrations.length;
}

export async function disposePluginSlot(container) {
  const cleanups = slotCleanups.get(container) || [];
  slotCleanups.delete(container);
  cleanups.splice(0).forEach(cleanup => {
    try { cleanup(); } catch (error) { console.warn("[NexusPipeline] 插件 slot 清理失败", error); }
  });
}

async function notifyLifecycle(kind, payload) {
  const handlers = lifecycle.get(kind) || [];
  for (const handler of handlers.slice()) {
    try { await handler(payload); } catch (error) { console.warn(`[NexusPipeline] 插件生命周期失败：${kind}`, error); }
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
