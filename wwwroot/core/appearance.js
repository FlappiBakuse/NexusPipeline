import { api } from "./api.js";

const THEME_KEY = "nexus-theme";
const LEGACY_THEME_KEY = "nexus-appearance-theme";
const WALLPAPER_KEY = "nexus-appearance-wallpaper";
const DB_NAME = "nexus-appearance";
const DB_VERSION = 1;
const STORE_NAME = "wallpaper";
const themes = new Map();
const appliedThemeTokens = new Set();
const appliedWallpaperTokens = new Set();
const adaptiveWallpaperTokens = new Set([
  "--accent", "--accent-strong", "--accent-alt", "--accent-soft", "--on-accent", "--focus", "--mask",
  "--wallpaper-card-dark", "--wallpaper-card-dark-soft", "--wallpaper-card-dark-hover", "--wallpaper-card-dark-border",
  "--wallpaper-card-light", "--wallpaper-card-light-soft", "--wallpaper-card-light-hover", "--wallpaper-card-light-border",
]);
const wallpaperSubscribers = new Set();
let wallpaperUrl = null;
let baseTheme = "system";
let serverSnapshot = null;
let initPromise = null;
let pollTimer = null;
let rotationTimer = null;

function safeStorageGet(key) {
  try { return localStorage.getItem(key); } catch { return null; }
}

function safeStorageSet(key, value) {
  try { localStorage.setItem(key, value); } catch { /* 存储不可用时保持当前会话外观。 */ }
}

function authHeaders(extra = {}) {
  const headers = { ...extra };
  const token = safeStorageGet("nexus-token");
  if (token) headers.Authorization = "Bearer " + token;
  return headers;
}

function validTokenName(name) {
  return typeof name === "string" && /^--[a-zA-Z0-9_-]{1,96}$/.test(name);
}

function validTokenValue(value) {
  return typeof value === "string" && value.length <= 4096 && !/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(value);
}

function validateTokens(tokens = {}) {
  if (!tokens || typeof tokens !== "object") throw new TypeError("主题 tokens 无效");
  Object.entries(tokens).forEach(([name, value]) => {
    if (!validTokenName(name) || !validTokenValue(value)) throw new TypeError(`主题 token 无效：${name}`);
  });
}

function setThemeTokens(tokens = {}) {
  validateTokens(tokens);
  Object.entries(tokens).forEach(([name, value]) => {
    document.documentElement.style.setProperty(name, value);
    appliedThemeTokens.add(name);
  });
}

function clearThemeTokens() {
  appliedThemeTokens.forEach(name => document.documentElement.style.removeProperty(name));
  appliedThemeTokens.clear();
}

function setWallpaperTokens(tokens = {}) {
  validateTokens(tokens);
  clearWallpaperTokens();
  Object.entries(tokens).forEach(([name, value]) => {
    if (!adaptiveWallpaperTokens.has(name)) return;
    (document.body || document.documentElement).style.setProperty(name, value);
    appliedWallpaperTokens.add(name);
  });
}

function clearWallpaperTokens() {
  appliedWallpaperTokens.forEach(name => {
    document.documentElement.style.removeProperty(name);
    document.body?.style.removeProperty(name);
  });
  appliedWallpaperTokens.clear();
}

function registerTheme(name, definition = {}) {
  const key = String(name || "").trim();
  if (!/^[a-zA-Z0-9_-]{1,64}$/.test(key)) throw new TypeError("主题名称无效");
  const tokens = definition.tokens || definition;
  validateTokens(tokens);
  themes.set(key, { ...definition, tokens: { ...tokens } });
  return { dispose: () => themes.delete(key) };
}

function applyBaseTheme() {
  clearThemeTokens();
  const definition = themes.get(baseTheme);
  if (definition) setThemeTokens(definition.tokens || {});
  const value = ["light", "dark", "system"].includes(baseTheme) ? baseTheme : "system";
  document.body.dataset.theme = value;
  document.body.dataset.appearanceTheme = baseTheme;
}

export function applyThemeValue(name) {
  const key = String(name || "system").trim();
  baseTheme = ["light", "dark", "system"].includes(key) || themes.has(key) ? key : "system";
  safeStorageSet(THEME_KEY, baseTheme);
  applyBaseTheme();
  return baseTheme;
}

export function initThemeValue() {
  const stored = safeStorageGet(THEME_KEY) || safeStorageGet(LEGACY_THEME_KEY) || "system";
  return applyThemeValue(stored);
}

export function cycleThemeValue() {
  return applyThemeValue(baseTheme === "system" ? "light" : baseTheme === "light" ? "dark" : "system");
}

function openDatabase() {
  return new Promise((resolve, reject) => {
    if (!window.indexedDB) { resolve(null); return; }
    const request = indexedDB.open(DB_NAME, DB_VERSION);
    request.onupgradeneeded = () => request.result.createObjectStore(STORE_NAME);
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error || new Error("IndexedDB unavailable"));
  });
}

async function storeWallpaper(blob) {
  const db = await openDatabase();
  if (!db) return;
  await new Promise((resolve, reject) => {
    const request = db.transaction(STORE_NAME, "readwrite").objectStore(STORE_NAME).put(blob, "current");
    request.onsuccess = resolve;
    request.onerror = () => reject(request.error);
  }).finally(() => db.close());
}

async function readWallpaper() {
  const db = await openDatabase();
  if (!db) return null;
  return new Promise((resolve, reject) => {
    const request = db.transaction(STORE_NAME, "readonly").objectStore(STORE_NAME).get("current");
    request.onsuccess = () => { db.close(); resolve(request.result || null); };
    request.onerror = () => { db.close(); reject(request.error); };
  });
}

function applyWallpaperUrl(url) {
  if (wallpaperUrl && wallpaperUrl.startsWith("blob:")) URL.revokeObjectURL(wallpaperUrl);
  wallpaperUrl = url;
  if (!url) {
    document.documentElement.style.removeProperty("--nexus-wallpaper-image");
    document.documentElement.style.removeProperty("--nexus-wallpaper-blur");
    document.documentElement.style.removeProperty("--nexus-wallpaper-dim");
    document.documentElement.style.removeProperty("--nexus-surface-opacity");
    document.body.removeAttribute("data-wallpaper");
    clearWallpaperTokens();
    return;
  }
  document.documentElement.style.setProperty("--nexus-wallpaper-image", `url(${JSON.stringify(url)})`);
  document.body.dataset.wallpaper = "on";
}

function setWallpaperEffects(effects = {}) {
  const blur = Math.max(0, Math.min(40, Number(effects.blurPx) || 0));
  const dim = Math.max(0, Math.min(80, Number(effects.dimPercent) || 0));
  const transparency = Math.max(0, Math.min(50, Number(effects.surfaceTransparencyPercent) || 0));
  document.documentElement.style.setProperty("--nexus-wallpaper-blur", `${blur}px`);
  document.documentElement.style.setProperty("--nexus-wallpaper-dim", `${dim / 100}`);
  document.documentElement.style.setProperty("--nexus-surface-opacity", `${100 - transparency}%`);
}

function clearWallpaper() {
  applyWallpaperUrl(null);
  safeStorageSet(WALLPAPER_KEY, "");
}

function hslToCss(h, s, l) {
  return `hsl(${Math.round(h)} ${Math.round(s)}% ${Math.round(l)}%)`;
}

function hslAlphaToCss(h, s, l, alpha) {
  return `hsl(${Math.round(h)} ${Math.round(s)}% ${Math.round(l)}% / ${alpha})`;
}

function relativeLuminance(r, g, b) {
  const linear = value => {
    const channel = value / 255;
    return channel <= 0.03928 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}

function rgbToHsl(r, g, b) {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b); const min = Math.min(r, g, b);
  let h = 0; let s = 0; const l = (max + min) / 2; const delta = max - min;
  if (delta) {
    s = delta / (1 - Math.abs(2 * l - 1));
    if (max === r) h = 60 * (((g - b) / delta) % 6);
    else if (max === g) h = 60 * ((b - r) / delta + 2);
    else h = 60 * ((r - g) / delta + 4);
  }
  return [(h + 360) % 360, s * 100, l * 100];
}

// 取缩略图平均色与主色的可读性推导；结果为普通实色 token，避免透明层叠造成文字边界不清。
export async function derivePalette(blob) {
  if (!(blob instanceof Blob)) throw new TypeError("壁纸数据无效");
  const bitmap = await createImageBitmap(blob);
  const canvas = document.createElement("canvas");
  const size = 64;
  canvas.width = size; canvas.height = size;
  const context = canvas.getContext("2d", { willReadFrequently: true });
  context.drawImage(bitmap, 0, 0, size, size);
  bitmap.close?.();
  const pixels = context.getImageData(0, 0, size, size).data;
  let red = 0; let green = 0; let blue = 0; let weight = 0;
  for (let i = 0; i < pixels.length; i += 4) {
    const alpha = pixels[i + 3] / 255;
    red += pixels[i] * alpha; green += pixels[i + 1] * alpha; blue += pixels[i + 2] * alpha; weight += alpha;
  }
  red = Math.round(red / Math.max(1, weight)); green = Math.round(green / Math.max(1, weight)); blue = Math.round(blue / Math.max(1, weight));
  const light = relativeLuminance(red, green, blue) < 0.42;
  const [hue, saturation] = rgbToHsl(red, green, blue);
  const cardSaturation = Math.max(10, Math.min(32, saturation * 0.34 + 8));
  const accent = hslToCss(hue, Math.max(48, Math.min(78, saturation + 18)), light ? 66 : 42);
  const accentStrong = hslToCss(hue, Math.max(52, Math.min(84, saturation + 25)), light ? 74 : 34);
  return {
    "--accent": accent,
    "--accent-strong": accentStrong,
    "--accent-alt": hslToCss((hue + 32) % 360, 64, light ? 68 : 38),
    "--accent-soft": hslAlphaToCss(hue, Math.max(48, Math.min(78, saturation + 18)), light ? 66 : 42, light ? 0.2 : 0.18),
    "--on-accent": "#ffffff",
    "--mask": light ? "rgba(4, 10, 20, .44)" : "rgba(255, 255, 255, .42)",
    "--focus": `0 0 0 3px ${hslAlphaToCss(hue, Math.max(48, Math.min(78, saturation + 18)), light ? 66 : 42, 0.32)}`,
    "--wallpaper-card-dark": hslToCss(hue, cardSaturation, 16),
    "--wallpaper-card-dark-soft": hslToCss(hue, Math.min(36, cardSaturation + 2), 21),
    "--wallpaper-card-dark-hover": hslToCss(hue, Math.min(40, cardSaturation + 5), 26),
    "--wallpaper-card-dark-border": hslAlphaToCss(hue, Math.min(44, cardSaturation + 10), 64, 0.32),
    "--wallpaper-card-light": hslToCss(hue, cardSaturation, 97),
    "--wallpaper-card-light-soft": hslToCss(hue, Math.min(36, cardSaturation + 2), 93),
    "--wallpaper-card-light-hover": hslToCss(hue, Math.min(40, cardSaturation + 5), 89),
    "--wallpaper-card-light-border": hslAlphaToCss(hue, Math.min(44, cardSaturation + 10), 42, 0.24),
  };
}

async function fetchAssetBlob(asset) {
  const response = await fetch(asset.url, { headers: authHeaders(), cache: "no-store" });
  if (!response.ok) throw new Error(`壁纸读取失败（HTTP ${response.status}）`);
  return response.blob();
}

async function applyServerSnapshot(snapshot, caller = snapshot?.provider?.pluginName || "") {
  serverSnapshot = snapshot;
  if (!snapshot?.effectiveEnabled || !snapshot.currentId) {
    clearWallpaper();
    return snapshot;
  }
  const asset = (snapshot.assets || []).find(item => item.id === snapshot.currentId);
  if (!asset) {
    clearWallpaper();
    return snapshot;
  }
  const blob = await fetchAssetBlob(asset);
  applyWallpaperUrl(URL.createObjectURL(blob));
  setWallpaperEffects(snapshot.effects);
  if (asset.paletteVersion === 3 && asset.palette) {
    setWallpaperTokens(asset.palette);
  } else {
    const palette = await derivePalette(blob);
    setWallpaperTokens(palette);
    if (caller) {
      api("PUT", `/api/appearance-assets/${encodeURIComponent(asset.id)}/palette?plugin=${encodeURIComponent(caller)}`, palette).catch(() => {});
    }
  }
  safeStorageSet(WALLPAPER_KEY, `server:${asset.id}`);
  scheduleRotation(snapshot, caller);
  return snapshot;
}

function scheduleRotation(snapshot, caller) {
  if (rotationTimer !== null) clearTimeout(rotationTimer);
  rotationTimer = null;
  if (!snapshot?.effectiveEnabled || snapshot.rotation?.mode !== "timer" || !snapshot.nextSwitchAt) return;
  const delay = Math.max(250, new Date(snapshot.nextSwitchAt).getTime() - Date.now());
  rotationTimer = setTimeout(async () => {
    try { await refreshAppearance(caller); } catch { /* 下次轮询继续尝试。 */ }
  }, delay);
}

function notifyWallpaperSubscribers(snapshot) {
  wallpaperSubscribers.forEach(callback => {
    try { callback(snapshot); } catch { /* 单个订阅者异常不影响外观刷新。 */ }
  });
}

async function refreshAppearance(caller = "", rotateOnStartup = false) {
  const snapshot = await api("GET", "/api/appearance");
  let effective = snapshot;
  const pluginName = caller || snapshot?.provider?.pluginName || "";
  if (rotateOnStartup && snapshot?.effectiveEnabled && snapshot.rotation?.mode === "startup" && pluginName) {
    effective = await api("POST", `/api/appearance/rotation/startup?plugin=${encodeURIComponent(pluginName)}`);
  }
  try {
    await applyServerSnapshot(effective, pluginName);
  } catch (error) {
    console.warn("[NexusPipeline] 自定义壁纸应用失败", error);
  }
  notifyWallpaperSubscribers(effective);
  return effective;
}

function startWallpaperPolling() {
  if (pollTimer !== null) return;
  pollTimer = setInterval(async () => {
    try { await refreshAppearance(serverSnapshot?.provider?.pluginName || ""); } catch { /* 服务暂不可用时保留当前页面状态。 */ }
  }, 30_000);
}

async function rawRequest(path, method, body, headers = {}) {
  const response = await fetch(path, { method, headers: authHeaders(headers), body });
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new Error(data?.error || `HTTP ${response.status}`);
  return data;
}

function createWallpaperStore(pluginName) {
  const caller = String(pluginName || "").trim();
  const query = caller ? `?plugin=${encodeURIComponent(caller)}` : "";
  return Object.freeze({
    get: () => api("GET", "/api/appearance"),
    upload: async (blob, metadata = {}) => rawRequest(
      `/api/appearance-upload${query}${query ? "&" : "?"}name=${encodeURIComponent(metadata.name || blob.name || "wallpaper")}`,
      "POST",
      blob,
      { "Content-Type": blob.type || "application/octet-stream", "X-Nexus-Original-Name": metadata.name || blob.name || "wallpaper" }),
    remove: id => api("DELETE", `/api/appearance-assets/${encodeURIComponent(id)}${query}`),
    save: async config => {
      const patch = { ...config, provider: { ...(config?.provider || {}), pluginName: caller, enabled: config?.provider?.enabled !== false } };
      const snapshot = await api("PUT", "/api/appearance", patch);
      await applyServerSnapshot(snapshot, caller).catch(error => console.warn("[NexusPipeline] 壁纸刷新失败", error));
      return snapshot;
    },
    savePalette: async (id, palette) => {
      const snapshot = await api("PUT", `/api/appearance-assets/${encodeURIComponent(id)}/palette${query}`, palette);
      await applyServerSnapshot(snapshot, caller).catch(() => {});
      return snapshot;
    },
    startup: () => api("POST", `/api/appearance/rotation/startup${query}`),
    refresh: () => refreshAppearance(caller),
    subscribe: callback => {
      if (typeof callback !== "function") throw new TypeError("壁纸订阅处理器无效");
      wallpaperSubscribers.add(callback);
      startWallpaperPolling();
      return { dispose: () => wallpaperSubscribers.delete(callback) };
    },
  });
}

export async function setWallpaper(source) {
  if (source instanceof Blob) {
    if (source.size > 20 * 1024 * 1024) throw new Error("壁纸文件不能超过 20MB");
    await storeWallpaper(source);
    applyWallpaperUrl(URL.createObjectURL(source));
    safeStorageSet(WALLPAPER_KEY, "indexeddb");
    return;
  }
  const url = String(source || "").trim();
  if (!url || /[\u0000-\u001f]/.test(url)) throw new TypeError("壁纸地址无效");
  let parsed;
  try { parsed = new URL(url, location.href); } catch { throw new TypeError("壁纸地址无效"); }
  if (!["http:", "https:", "blob:", "data:"].includes(parsed.protocol)) throw new TypeError("壁纸地址协议不受支持");
  applyWallpaperUrl(parsed.href);
  safeStorageSet(WALLPAPER_KEY, parsed.href);
}

export async function initAppearance() {
  if (initPromise) return initPromise;
  initPromise = (async () => {
    initThemeValue();
    try {
      const snapshot = await refreshAppearance("", true);
      startWallpaperPolling();
      return snapshot;
    } catch {
      const savedWallpaper = safeStorageGet(WALLPAPER_KEY);
      if (savedWallpaper === "indexeddb") {
        try {
          const blob = await readWallpaper();
          if (blob) applyWallpaperUrl(URL.createObjectURL(blob));
        } catch { /* IndexedDB 不可用时保持无壁纸状态。 */ }
      } else if (savedWallpaper && !savedWallpaper.startsWith("server:")) {
        try { await setWallpaper(savedWallpaper); } catch { clearWallpaper(); }
      }
      startWallpaperPolling();
      return null;
    }
  })();
  return initPromise;
}

export { clearWallpaper, registerTheme, setThemeTokens as setTokens, clearThemeTokens as clearTokens, refreshAppearance };

export function createAppearanceHost(pluginName) {
  return Object.freeze({
    registerTheme,
    applyTheme: applyThemeValue,
    setTokens: setThemeTokens,
    clearTokens: clearThemeTokens,
    setWallpaper,
    clearWallpaper,
    init: initAppearance,
    derivePalette,
    wallpaperStore: createWallpaperStore(pluginName),
  });
}

export const appearance = createAppearanceHost("");
