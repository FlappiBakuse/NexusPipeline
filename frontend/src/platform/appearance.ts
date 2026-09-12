import { t } from "./i18n";

export type ThemeValue = string;

/** 宿主通用背景表面参数；背景图片的来源与切换策略由调用方（插件）决定。 */
export interface BackgroundSurface {
  url: string;
  blurPx?: number;
  dimPercent?: number;
  surfaceTransparencyPercent?: number;
  secondarySurfaceTransparency?: boolean;
}

const THEME_KEY = "nexus-theme";
const themes = new Map<string, { tokens: Record<string, string> }>();
const appliedThemeTokens = new Set<string>();
const appliedAppearanceTokens = new Set<string>();
let baseTheme = "system";
let backgroundUrl: string | null = null;

function safeStorageGet(key: string): string | null {
  try { return localStorage.getItem(key); } catch { return null; }
}

function safeStorageSet(key: string, value: string): void {
  try { localStorage.setItem(key, value); } catch { /* 存储不可用时保持当前会话外观。 */ }
}

function validTokenName(name: unknown): boolean {
  return typeof name === "string" && /^--[a-zA-Z0-9_-]{1,96}$/.test(name);
}

function validTokenValue(value: unknown): boolean {
  return typeof value === "string" && value.length <= 4096 && !/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(value);
}

function notifyAppearanceChanged(): void {
  document.dispatchEvent(new CustomEvent("nexus:appearance-changed"));
}

function validateTokens(tokens: Record<string, string> = {}): void {
  if (!tokens || typeof tokens !== "object") throw new TypeError(t("common.theme_tokens_invalid"));
  Object.entries(tokens).forEach(([name, value]) => {
    if (!validTokenName(name) || !validTokenValue(value)) throw new TypeError(t("common.theme_token_invalid", { name }));
  });
}

function setThemeTokens(tokens: Record<string, string> = {}): void {
  validateTokens(tokens);
  Object.entries(tokens).forEach(([name, value]) => {
    document.documentElement.style.setProperty(name, value);
    appliedThemeTokens.add(name);
  });
}

function clearThemeTokens(): void {
  appliedThemeTokens.forEach(name => document.documentElement.style.removeProperty(name));
  appliedThemeTokens.clear();
}

/**
 * 调用方（插件）提供的通用外观 token。应用在 body 上，因此优先于主题 token 且不随主题切换被清除；
 * 只有显式 clearTokens 或替换新集合时才移除。
 */
function setAppearanceTokens(tokens: Record<string, string> = {}): void {
  validateTokens(tokens);
  clearAppearanceTokens();
  Object.entries(tokens).forEach(([name, value]) => {
    (document.body || document.documentElement).style.setProperty(name, value);
    appliedAppearanceTokens.add(name);
  });
  notifyAppearanceChanged();
}

function clearAppearanceTokens(): void {
  appliedAppearanceTokens.forEach(name => {
    document.body?.style.removeProperty(name);
    document.documentElement.style.removeProperty(name);
  });
  appliedAppearanceTokens.clear();
  notifyAppearanceChanged();
}

function registerTheme(name: string, definition: { tokens?: Record<string, string> } = {}): { dispose: () => void } {
  const key = String(name || "").trim();
  if (!/^[a-zA-Z0-9_-]{1,64}$/.test(key)) throw new TypeError(t("common.theme_name_invalid"));
  const tokens = definition?.tokens || (definition as unknown as Record<string, string>);
  validateTokens(tokens);
  themes.set(key, { tokens: { ...tokens } });
  return { dispose: () => themes.delete(key) };
}

function applyBaseTheme(): void {
  clearThemeTokens();
  const definition = themes.get(baseTheme);
  if (definition) setThemeTokens(definition.tokens || {});
  const value = ["light", "dark", "system"].includes(baseTheme) ? baseTheme : "system";
  document.body.dataset.theme = value;
  document.body.dataset.appearanceTheme = baseTheme;
  notifyAppearanceChanged();
}

export function applyThemeValue(name: string): ThemeValue {
  const key = String(name || "system").trim();
  baseTheme = ["light", "dark", "system"].includes(key) || themes.has(key) ? key : "system";
  safeStorageSet(THEME_KEY, baseTheme);
  applyBaseTheme();
  return baseTheme;
}

export function initThemeValue(): ThemeValue {
  const stored = safeStorageGet(THEME_KEY) || "system";
  return applyThemeValue(stored);
}

export function cycleThemeValue(): ThemeValue {
  return applyThemeValue(baseTheme === "system" ? "light" : baseTheme === "light" ? "dark" : "system");
}

function clamp(value: unknown, min: number, max: number, fallback: number): number {
  const number = Number(value);
  if (!Number.isFinite(number)) return fallback;
  return Math.max(min, Math.min(max, number));
}

/**
 * 释放不再使用的背景地址：Blob 地址由外观表面托管，只有替换为新地址或清除背景时才回收，
 * 避免多次切换壁纸累积失效的 Object URL。
 */
function releaseBackgroundUrl(keep: string | null): void {
  if (backgroundUrl && backgroundUrl !== keep && backgroundUrl.startsWith("blob:")) {
    URL.revokeObjectURL(backgroundUrl);
  }
}

/** 应用背景图片与显示效果；url 为空或协议不安全时按非法参数拒绝。 */
export function setBackgroundSurface(surface: BackgroundSurface): void {
  const url = String(surface?.url || "").trim();
  if (!url || /[\u0000-\u001f]/.test(url)) throw new TypeError(t("common.appearance_url_invalid"));
  let parsed: URL;
  try { parsed = new URL(url, location.href); } catch { throw new TypeError(t("common.appearance_url_invalid")); }
  if (!["http:", "https:", "blob:", "data:"].includes(parsed.protocol)) throw new TypeError(t("common.error.appearance_url_protocol"));
  releaseBackgroundUrl(parsed.href);
  backgroundUrl = parsed.href;
  const blur = clamp(surface?.blurPx, 0, 40, 0);
  const dim = clamp(surface?.dimPercent, 0, 80, 0);
  const transparency = clamp(surface?.surfaceTransparencyPercent, 0, 50, 0);
  document.documentElement.style.setProperty("--nexus-wallpaper-image", `url(${JSON.stringify(backgroundUrl)})`);
  document.documentElement.style.setProperty("--nexus-wallpaper-blur", `${blur}px`);
  document.documentElement.style.setProperty("--nexus-wallpaper-dim", `${dim / 100}`);
  document.documentElement.style.setProperty("--nexus-surface-opacity", `${100 - transparency}%`);
  document.body.dataset.wallpaper = "on";
  document.body.dataset.secondarySurfaceTransparency = surface?.secondarySurfaceTransparency === false ? "off" : "on";
  notifyAppearanceChanged();
}

/** 移除背景图片与显示效果，恢复宿主默认表面。 */
export function clearBackgroundSurface(): void {
  if (backgroundUrl && backgroundUrl.startsWith("blob:")) URL.revokeObjectURL(backgroundUrl);
  backgroundUrl = null;
  document.documentElement.style.removeProperty("--nexus-wallpaper-image");
  document.documentElement.style.removeProperty("--nexus-wallpaper-blur");
  document.documentElement.style.removeProperty("--nexus-wallpaper-dim");
  document.documentElement.style.removeProperty("--nexus-surface-opacity");
  document.body.removeAttribute("data-wallpaper");
  document.body.dataset.secondarySurfaceTransparency = "on";
  notifyAppearanceChanged();
}

/** 初始化宿主外观：读取已保存主题并广播一次外观变更。可重复调用。 */
export async function initAppearance(): Promise<void> {
  initThemeValue();
}

export function createAppearanceHost() {
  return Object.freeze({
    registerTheme,
    applyTheme: applyThemeValue,
    setTokens: setAppearanceTokens,
    clearTokens: clearAppearanceTokens,
    setBackground: setBackgroundSurface,
    clearBackground: clearBackgroundSurface,
  });
}
