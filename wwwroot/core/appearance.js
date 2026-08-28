const THEME_KEY = "nexus-appearance-theme";
const WALLPAPER_KEY = "nexus-appearance-wallpaper";
const DB_NAME = "nexus-appearance";
const DB_VERSION = 1;
const STORE_NAME = "wallpaper";
const themes = new Map();
const appliedTokens = new Set();
let wallpaperUrl = null;

function safeStorageGet(key) {
  try { return localStorage.getItem(key); } catch { return null; }
}

function safeStorageSet(key, value) {
  try { localStorage.setItem(key, value); } catch { /* 存储不可用时保持当前会话外观。 */ }
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

function setTokens(tokens = {}) {
  validateTokens(tokens);
  Object.entries(tokens).forEach(([name, value]) => {
    document.documentElement.style.setProperty(name, value);
    appliedTokens.add(name);
  });
}

function clearTokens() {
  appliedTokens.forEach(name => document.documentElement.style.removeProperty(name));
  appliedTokens.clear();
}

function registerTheme(name, definition = {}) {
  const key = String(name || "").trim();
  if (!/^[a-zA-Z0-9_-]{1,64}$/.test(key)) throw new TypeError("主题名称无效");
  const tokens = definition.tokens || definition;
  validateTokens(tokens);
  themes.set(key, { ...definition, tokens: { ...tokens } });
  return { dispose: () => themes.delete(key) };
}

function applyTheme(name) {
  const key = String(name || "system").trim();
  const definition = themes.get(key);
  clearTokens();
  if (definition) setTokens(definition.tokens || {});
  document.body.dataset.theme = ["light", "dark", "system"].includes(key) ? key : "system";
  document.body.dataset.appearanceTheme = key;
  safeStorageSet(THEME_KEY, key);
  return key;
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
  if (wallpaperUrl) URL.revokeObjectURL(wallpaperUrl);
  wallpaperUrl = url;
  if (!url) {
    document.documentElement.style.removeProperty("--nexus-wallpaper-image");
    document.body.removeAttribute("data-wallpaper");
    return;
  }
  document.documentElement.style.setProperty("--nexus-wallpaper-image", `url(${JSON.stringify(url)})`);
  document.body.dataset.wallpaper = "on";
}

async function setWallpaper(source) {
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
  if (!['http:', 'https:', 'blob:', 'data:'].includes(parsed.protocol)) throw new TypeError("壁纸地址协议不受支持");
  applyWallpaperUrl(parsed.href);
  safeStorageSet(WALLPAPER_KEY, parsed.href);
}

function clearWallpaper() {
  applyWallpaperUrl(null);
  safeStorageSet(WALLPAPER_KEY, "");
}

export async function initAppearance() {
  const savedTheme = safeStorageGet(THEME_KEY);
  if (savedTheme && themes.has(savedTheme)) applyTheme(savedTheme);
  const savedWallpaper = safeStorageGet(WALLPAPER_KEY);
  if (savedWallpaper === "indexeddb") {
    try {
      const blob = await readWallpaper();
      if (blob) applyWallpaperUrl(URL.createObjectURL(blob));
    } catch {
      // IndexedDB 不可用时保留无壁纸状态。
    }
  } else if (savedWallpaper) {
    try { await setWallpaper(savedWallpaper); } catch { clearWallpaper(); }
  }
}

export const appearance = Object.freeze({
  registerTheme,
  applyTheme,
  setTokens,
  clearTokens,
  setWallpaper,
  clearWallpaper,
  init: initAppearance,
});
