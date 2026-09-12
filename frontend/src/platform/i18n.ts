const STORAGE_KEY = "nexus-locale";
const DEFAULT_LOCALE = "zh-CN";
const ENGLISH_LOCALE = "en-US";

interface LocaleRegistry {
  default: string;
  supported: string[];
  names: Record<string, string>;
}

export type LocaleMessages = Record<string, string>;

/**
 * 内置注册表兜底：语言名称在 `frontend/public/i18n/locales.json` 中按各自语言书写，
 * 加载成功后以该资源为唯一来源（`getLocaleOptions` 从注册表读取）。
 */
let localeRegistry: LocaleRegistry = {
  default: DEFAULT_LOCALE,
  supported: [DEFAULT_LOCALE, ENGLISH_LOCALE],
  names: { [DEFAULT_LOCALE]: DEFAULT_LOCALE, [ENGLISH_LOCALE]: ENGLISH_LOCALE },
};
let currentLocale = readStoredLocale() || detectLocale();
let messages: LocaleMessages = Object.create(null);
let defaultMessages: LocaleMessages = Object.create(null);
let registryLoaded = false;
const explicitTextValues = new WeakMap<Element, string>();
const explicitAttributeValues = new WeakMap<Element, Record<string, string>>();

function syncDocumentLocale() {
  if (typeof document !== "undefined" && document.documentElement) {
    document.documentElement.lang = currentLocale;
  }
}

function canonicalLocale(value: unknown): string {
  const candidate = String(value || "").trim().replaceAll("_", "-").toLowerCase();
  const supported = localeRegistry.supported || [DEFAULT_LOCALE, ENGLISH_LOCALE];
  const exact = supported.find(locale => String(locale).toLowerCase() === candidate);
  if (exact) return exact;
  const language = candidate.split("-", 1)[0];
  const match = supported.find(locale => String(locale).toLowerCase().split("-", 1)[0] === language);
  return match || localeRegistry.default || DEFAULT_LOCALE;
}

function readStoredLocale(): string | null {
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

function detectLocale(): string {
  const browserLanguages = typeof navigator !== "undefined" && Array.isArray(navigator.languages)
    ? navigator.languages
    : typeof navigator !== "undefined" && navigator.language
      ? [navigator.language]
      : [];
  const candidates = browserLanguages;
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

export function getLocale(): string {
  return currentLocale;
}

export function getSupportedLocales(): string[] {
  return [...(localeRegistry.supported || [DEFAULT_LOCALE, ENGLISH_LOCALE])];
}

export function getLocaleOptions(): Array<{ id: string; nativeName: string }> {
  return getSupportedLocales().map(id => ({
    id,
    nativeName: localeRegistry.names?.[id] || id,
  }));
}

export async function loadLocale(value: unknown = currentLocale): Promise<boolean> {
  if (!registryLoaded) {
    try {
      const registryResponse = await fetch("i18n/locales.json", { cache: "no-store" });
      if (registryResponse.ok) {
        const candidate = await registryResponse.json();
        const entries = Array.isArray(candidate?.supported)
          ? candidate.supported.map((item: unknown) => typeof item === "string" ? { id: item, nativeName: item } : item).filter((item: { id?: string }) => item?.id)
          : [];
        if (entries.length) {
          localeRegistry = {
            default: String(candidate.default || entries[0].id),
            supported: entries.map((item: { id: string }) => String(item.id)),
            names: Object.fromEntries(entries.map((item: { id: string; nativeName?: string }) => [String(item.id), String(item.nativeName || item.id)])),
          };
        }
      }
    } catch {
      // 内置注册表保持页面可用。
    }
    registryLoaded = true;
  }
  currentLocale = canonicalLocale(value);
  const loadResource = async (locale: string): Promise<LocaleMessages | null> => {
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
  syncDocumentLocale();
  applyTranslations();
  return loaded;
}

export async function setLocale(value: unknown): Promise<string> {
  const next = canonicalLocale(value);
  try {
    localStorage.setItem(STORAGE_KEY, next);
  } catch {
  }
  if (next !== currentLocale || !Object.keys(messages).length) await loadLocale(next);
  else {
    syncDocumentLocale();
    applyTranslations();
  }
  return currentLocale;
}

export function t(key: string, args: Record<string, unknown> = {}, fallback = ""): string {
  let value: string | undefined = messages[key] ?? defaultMessages[key];
  if (value === undefined || value === null) value = fallback || key;
  let result = String(value);
  for (const [name, replacement] of Object.entries(args || {})) {
    result = result.replaceAll(`{${name}}`, String(replacement ?? ""));
  }
  return result;
}

export function formatNumber(value: number | bigint, options?: Intl.NumberFormatOptions): string {
  try { return new Intl.NumberFormat(currentLocale, options).format(value); } catch { return String(value ?? ""); }
}

export function formatDate(value: string | number | Date, options?: Intl.DateTimeFormatOptions): string {
  try { return new Intl.DateTimeFormat(currentLocale, options).format(new Date(value)); } catch { return String(value ?? ""); }
}

export function formatTime(value: string | number | Date, options?: Intl.DateTimeFormatOptions): string {
  try { return new Intl.DateTimeFormat(currentLocale, { timeStyle: "short", ...options }).format(new Date(value)); } catch { return String(value ?? ""); }
}

function normalizeListValues(values: unknown): string[] {
  return (Array.isArray(values) ? values : [values])
    .map(value => String(value ?? "").trim())
    .filter(Boolean);
}

/** 使用当前界面的语言规则连接用户可见列表，避免业务视图内写死语言特有标点。 */
export function formatList(values: unknown, options: Intl.ListFormatOptions = {}): string {
  const items = normalizeListValues(values);
  if (items.length < 2) return items[0] || "";
  try {
    return new Intl.ListFormat(currentLocale, {
      style: "long",
      type: "conjunction",
      ...options,
    }).format(items);
  } catch {
    return items.join(currentLocale.toLowerCase().startsWith("zh") ? "、" : ", ");
  }
}

export function applyTranslations(root: ParentNode | null = typeof document !== "undefined" ? document : null): void {
  if (!root?.querySelectorAll) return;
  root.querySelectorAll<HTMLElement>("[data-i18n]").forEach(element => {
    const fallback = explicitTextValues.get(element) ?? element.dataset.i18nFallback ?? element.textContent;
    explicitTextValues.set(element, fallback);
    element.textContent = t(String(element.dataset.i18n || ""), {}, fallback);
  });
  root.querySelectorAll<HTMLElement>("[data-i18n-title]").forEach(element => {
    const fallback = getExplicitAttributeFallback(element, "title");
    element.title = t(String(element.dataset.i18nTitle || ""), {}, fallback);
  });
  root.querySelectorAll<HTMLElement>("[data-i18n-aria-label]").forEach(element => {
    const fallback = getExplicitAttributeFallback(element, "aria-label");
    element.setAttribute("aria-label", t(String(element.dataset.i18nAriaLabel || ""), {}, fallback));
  });
  root.querySelectorAll<HTMLElement>("[data-i18n-placeholder]").forEach(element => {
    const fallback = getExplicitAttributeFallback(element, "placeholder");
    element.setAttribute("placeholder", t(String(element.dataset.i18nPlaceholder || ""), {}, fallback));
  });
}

function getExplicitAttributeFallback(element: Element, attribute: string): string {
  let values = explicitAttributeValues.get(element);
  if (!values) {
    values = Object.create(null) as Record<string, string>;
    explicitAttributeValues.set(element, values);
  }
  if (values[attribute] === undefined) values[attribute] = element.getAttribute(attribute) || "";
  return values[attribute];
}
