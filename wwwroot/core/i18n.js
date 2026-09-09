const STORAGE_KEY = "nexus-locale";
const DEFAULT_LOCALE = "zh-CN";
const ENGLISH_LOCALE = "en-US";

let localeRegistry = {
  default: DEFAULT_LOCALE,
  supported: [DEFAULT_LOCALE, ENGLISH_LOCALE],
  names: { [DEFAULT_LOCALE]: "\u7b80\u4f53\u4e2d\u6587", [ENGLISH_LOCALE]: "English" },
};
let currentLocale = readStoredLocale() || detectLocale();
let messages = Object.create(null);
let defaultMessages = Object.create(null);
let registryLoaded = false;
const listeners = new Set();
const explicitTextValues = new WeakMap();
const explicitAttributeValues = new WeakMap();
const noResourceFallbacks = {
  "ui.success": "\u6210\u529f",
  "ui.partially_failed": "\u90e8\u5206\u5931\u8d25",
  "ui.running": "\u8fd0\u884c\u4e2d",
  "ui.cancelled": "\u5df2\u53d6\u6d88",
  "ui.skipped": "\u5df2\u8df3\u8fc7",
  "ui.error": "\u9519\u8bef",
  "ui.failed": "\u5931\u8d25",
  "ui.value_itemsvalue": "\u5171 {total} \u6761{range}",
  "ui.value_value": "\uff0c\u7b2c {from}-{to} \u6761",
  "ui.previous": "\u4e0a\u4e00\u9875",
  "ui.next": "\u4e0b\u4e00\u9875",
};

function canonicalLocale(value) {
  const candidate = String(value || "").trim().replaceAll("_", "-").toLowerCase();
  const supported = localeRegistry.supported || [DEFAULT_LOCALE, ENGLISH_LOCALE];
  const exact = supported.find(locale => String(locale).toLowerCase() === candidate);
  if (exact) return exact;
  const language = candidate.split("-", 1)[0];
  const match = supported.find(locale => String(locale).toLowerCase().split("-", 1)[0] === language);
  return match || localeRegistry.default || DEFAULT_LOCALE;
}

function readStoredLocale() {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    const raw = String(stored || "").trim();
    if (!raw) return null;
    const candidate = canonicalLocale(raw);
    const supported = localeRegistry.supported || [];
    if (supported.some(locale => String(locale).toLowerCase() === candidate.toLowerCase())) return candidate;
    return null;
  } catch {
    return null;
  }
}

function detectLocale() {
  const candidates = Array.isArray(navigator.languages) ? navigator.languages : [navigator.language];
  for (const candidate of candidates) {
    const raw = String(candidate || "").trim();
    if (!raw) continue;
    const locale = canonicalLocale(raw);
    const language = raw.replaceAll("_", "-").toLowerCase().split("-", 1)[0];
    const supported = localeRegistry.supported || [];
    if (supported.some(item => {
      const normalized = String(item).toLowerCase();
      return normalized === raw.replaceAll("_", "-").toLowerCase()
        || normalized.split("-", 1)[0] === language;
    })) return locale;
  }
  return DEFAULT_LOCALE;
}

export function getLocale() {
  return currentLocale;
}

export function getSupportedLocales() {
  return [...(localeRegistry.supported || [DEFAULT_LOCALE, ENGLISH_LOCALE])];
}

export function getLocaleOptions() {
  return getSupportedLocales().map(id => ({
    id,
    nativeName: localeRegistry.names?.[id] || id,
  }));
}

export function resolveLocale(value) {
  return canonicalLocale(value);
}

export function localeHeaders() {
  return { "X-Nexus-Locale": currentLocale };
}

export async function loadLocale(value = currentLocale) {
  if (!registryLoaded) {
    try {
      const registryResponse = await fetch("i18n/locales.json", { cache: "no-store" });
      if (registryResponse.ok) {
        const candidate = await registryResponse.json();
        const entries = Array.isArray(candidate?.supported)
          ? candidate.supported.map(item => typeof item === "string" ? { id: item, nativeName: item } : item).filter(item => item?.id)
          : [];
        if (entries.length) {
          localeRegistry = {
            default: String(candidate.default || entries[0].id),
            supported: entries.map(item => String(item.id)),
            names: Object.fromEntries(entries.map(item => [String(item.id), String(item.nativeName || item.id)])),
          };
        }
      }
    } catch {
      // 内置注册表保持页面可用。
    }
    registryLoaded = true;
  }
  currentLocale = canonicalLocale(value);
  const loadResource = async locale => {
    try {
      const response = await fetch(`i18n/${encodeURIComponent(locale)}.json`, { cache: "no-store" });
      if (!response.ok) return null;
      const candidate = await response.json();
      return candidate && typeof candidate === "object" && !Array.isArray(candidate) ? candidate : null;
    } catch {
      return null;
    }
  };
  const loadedMessages = await loadResource(currentLocale);
  if (loadedMessages) messages = loadedMessages;
  const loadedDefault = currentLocale === localeRegistry.default
    ? loadedMessages
    : await loadResource(localeRegistry.default);
  if (loadedDefault) {
    defaultMessages = loadedDefault;
  }
  const loaded = Boolean(loadedMessages);
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
  let value = messages[key] ?? defaultMessages[key];
  if ((value === undefined || value === null) && typeof key === "string" && !key.includes(".")) {
    value = messages[`ui.${key}`] ?? defaultMessages[`ui.${key}`];
  }
  if (value === undefined || value === null) value = noResourceFallbacks[key];
  if (value === undefined || value === null) value = fallback || key;
  value = String(value);
  for (const [name, replacement] of Object.entries(args || {})) {
    value = value.replaceAll(`{${name}}`, String(replacement ?? ""));
  }
  return value;
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
