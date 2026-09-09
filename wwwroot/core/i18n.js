const STORAGE_KEY = "nexus-locale";
const DEFAULT_LOCALE = "zh-CN";
const ENGLISH_LOCALE = "en-US";
const SUPPORTED_LOCALES = new Set([DEFAULT_LOCALE, ENGLISH_LOCALE]);

let currentLocale = readStoredLocale() || detectLocale();
let messages = Object.create(null);
const listeners = new Set();
const legacyTextValues = new WeakMap();
const legacyAttributeValues = new WeakMap();
const explicitTextValues = new WeakMap();
const explicitAttributeValues = new WeakMap();

function canonicalLocale(value) {
  const candidate = String(value || "").trim().toLowerCase();
  if (candidate === "en" || candidate.startsWith("en-")) return ENGLISH_LOCALE;
  if (candidate === "zh" || candidate.startsWith("zh-")) return DEFAULT_LOCALE;
  return DEFAULT_LOCALE;
}

function readStoredLocale() {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    const raw = String(stored || "").trim().toLowerCase();
    if (raw === "en" || raw.startsWith("en-")) return ENGLISH_LOCALE;
    if (raw === "zh" || raw.startsWith("zh-")) return DEFAULT_LOCALE;
    return null;
  } catch {
    return null;
  }
}

function detectLocale() {
  const candidates = Array.isArray(navigator.languages) ? navigator.languages : [navigator.language];
  for (const candidate of candidates) {
    const locale = canonicalLocale(candidate);
    const raw = String(candidate || "").toLowerCase();
    if (raw === "en" || raw.startsWith("en-")) return locale;
    if (raw === "zh" || raw.startsWith("zh-")) return locale;
  }
  return DEFAULT_LOCALE;
}

export function getLocale() {
  return currentLocale;
}

export function getSupportedLocales() {
  return [DEFAULT_LOCALE, ENGLISH_LOCALE];
}

export function resolveLocale(value) {
  return canonicalLocale(value);
}

export function localeHeaders() {
  return { "X-Nexus-Locale": currentLocale };
}

export async function loadLocale(value = currentLocale) {
  currentLocale = canonicalLocale(value);
  let loaded = false;
  try {
    const response = await fetch(`i18n/${encodeURIComponent(currentLocale)}.json`, { cache: "no-store" });
    if (response.ok) {
      const candidate = await response.json();
      if (candidate && typeof candidate === "object" && !Array.isArray(candidate)) {
        messages = candidate;
        loaded = true;
      }
    }
  } catch {
    // 内置回退文案保持页面可用。
  }
  document.documentElement.lang = currentLocale;
  applyTranslations();
  listeners.forEach(listener => listener(currentLocale));
  return loaded;
}

export async function setLocale(value) {
  const next = canonicalLocale(value);
  try {
    localStorage.setItem(STORAGE_KEY, next);
  } catch {
  }
  if (next !== currentLocale || !Object.keys(messages).length) await loadLocale(next);
  else {
    document.documentElement.lang = currentLocale;
    applyTranslations();
  }
  return currentLocale;
}

export function subscribeLocale(listener) {
  if (typeof listener !== "function") return () => {};
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function t(key, args = {}, fallback = "") {
  let value = messages[key];
  if (value === undefined || value === null) value = fallback || key;
  value = String(value);
  for (const [name, replacement] of Object.entries(args || {})) {
    value = value.replaceAll(`{${name}}`, String(replacement ?? ""));
  }
  return value;
}

/** 将尚未迁移到显式 data-i18n 的固定界面文案通过资源表过渡；原始回退值保存在节点上，支持往返切换语言。 */
export function text(fallback, args = {}) {
  const value = String(fallback ?? "");
  return t(`legacy.${value}`, args, value);
}

/** 将包含已知固定片段的动态界面文案翻译到当前语言。用户数据保持原样。 */
export function translateText(value) {
  return translateLegacyFragments(String(value ?? ""));
}

function translateLegacyFragments(value) {
  let result = value;
  const entries = Object.entries(messages)
    .filter(([key, candidate]) => key.startsWith("legacy.") && String(candidate) !== key.slice(7))
    .map(([key, candidate]) => [key.slice(7), String(candidate)])
    .filter(([key]) => key.length > 1)
    .sort((left, right) => right[0].length - left[0].length);
  for (const [source, target] of entries) {
    if (!source.includes("{")) {
      result = result.replaceAll(source, target);
      continue;
    }
    const names = [];
    const pattern = source.replace(/[.*+?^${}()|[\]\\]/g, "\\$&").replace(/\\\{([^{}]+)\\\}/g, (_, name) => {
      names.push(name);
      return "([\\s\\S]*?)";
    });
    if (!names.length) continue;
    const expression = new RegExp(pattern, "g");
    result = result.replace(expression, (...matches) => {
      const captures = matches.slice(1, names.length + 1);
      return target.replace(/\{([^{}]+)\}/g, (_, name) => {
        const index = names.indexOf(name);
        return index >= 0 ? captures[index] : "";
      });
    });
  }
  return result;
}

export function formatNumber(value, options) {
  try { return new Intl.NumberFormat(currentLocale, options).format(value); } catch { return String(value ?? ""); }
}

export function formatDate(value, options) {
  try { return new Intl.DateTimeFormat(currentLocale, options).format(new Date(value)); } catch { return String(value ?? ""); }
}

export function formatTime(value, options) {
  try { return new Intl.DateTimeFormat(currentLocale, { timeStyle: "short", ...options }).format(new Date(value)); } catch { return String(value ?? ""); }
}

export function applyTranslations(root = document) {
  if (!root?.querySelectorAll) return;
  root.querySelectorAll("[data-i18n]").forEach(element => {
    const fallback = explicitTextValues.get(element) ?? element.dataset.i18nFallback ?? element.textContent;
    explicitTextValues.set(element, fallback);
    element.textContent = t(element.dataset.i18n, {}, fallback);
  });
  root.querySelectorAll("[data-i18n-title]").forEach(element => {
    const fallback = getExplicitAttributeFallback(element, "title");
    element.title = t(element.dataset.i18nTitle, {}, fallback);
  });
  root.querySelectorAll("[data-i18n-aria-label]").forEach(element => {
    const fallback = getExplicitAttributeFallback(element, "aria-label");
    element.setAttribute("aria-label", t(element.dataset.i18nAriaLabel, {}, fallback));
  });
  root.querySelectorAll("[data-i18n-placeholder]").forEach(element => {
    const fallback = getExplicitAttributeFallback(element, "placeholder");
    element.setAttribute("placeholder", t(element.dataset.i18nPlaceholder, {}, fallback));
  });
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const nodes = [];
  let node;
  while ((node = walker.nextNode())) nodes.push(node);
  nodes.forEach(textNode => {
    const parent = textNode.parentElement;
    if (!parent || parent.closest("script,style,[data-i18n]")) return;
    const raw = legacyTextValues.get(textNode) ?? textNode.nodeValue ?? "";
    const trimmed = raw.trim();
    if (!trimmed) return;
    const translated = translateLegacyFragments(trimmed);
    if (translated === trimmed) return;
    legacyTextValues.set(textNode, raw);
    const start = raw.indexOf(trimmed);
    const end = start + trimmed.length;
    textNode.nodeValue = raw.slice(0, start) + translated + raw.slice(end);
  });
  ["title", "aria-label", "placeholder", "data-help"].forEach(attribute => {
    root.querySelectorAll(`[${attribute}]`).forEach(element => {
      if (element.hasAttribute(`data-i18n-${attribute}`)) return;
      let values = legacyAttributeValues.get(element);
      if (!values) {
        values = Object.create(null);
        legacyAttributeValues.set(element, values);
      }
      const raw = values[attribute] ?? element.getAttribute(attribute) ?? "";
      if (!raw) return;
      const translated = translateLegacyFragments(raw);
      if (translated !== raw) {
        values[attribute] = raw;
        element.setAttribute(attribute, translated);
      }
    });
  });
}

function getExplicitAttributeFallback(element, attribute) {
  let values = explicitAttributeValues.get(element);
  if (!values) {
    values = Object.create(null);
    explicitAttributeValues.set(element, values);
  }
  if (values[attribute] === undefined) values[attribute] = element.getAttribute(attribute) || "";
  return values[attribute];
}
