import { pinyin } from "pinyin-pro";

export interface PluginViewState {
  query: string;
  kind: string;
  sortBy: string;
  direction: string;
}

export interface PluginViewRecord {
  name?: string;
  displayName?: string;
  description?: string;
  gameName?: string;
  kind?: string;
  authors?: Array<{ name?: string }>;
  tags?: string[];
  createdAt?: string;
  updatedAt?: string;
  locales?: Record<string, { displayName?: string; gameName?: string }>;
  [key: string]: unknown;
}

export type PluginViewTab = "local" | "store";
const DEFAULT_PLUGIN_VIEW_STATES: Readonly<Record<PluginViewTab, PluginViewState>> = Object.freeze({
  local: Object.freeze({ query: "", kind: "all", sortBy: "name", direction: "asc" }),
  store: Object.freeze({ query: "", kind: "all", sortBy: "updatedAt", direction: "desc" }),
});

const SUPPORTED_KINDS = new Set(["all", "managed-code", "data-specialized"]);
const SUPPORTED_SORTS = new Set(["name", "createdAt", "updatedAt"]);
const SUPPORTED_DIRECTIONS = new Set(["asc", "desc"]);

let pinyinCollator: Intl.Collator | null | undefined;
let fallbackCollator: Intl.Collator | null | undefined;

function getPinyinCollator(): Intl.Collator | null {
  if (pinyinCollator !== undefined) return pinyinCollator;
  try {
    pinyinCollator = new Intl.Collator("zh-CN-u-co-pinyin", {
      sensitivity: "base",
      numeric: true,
    });
  } catch {
    pinyinCollator = null;
  }
  return pinyinCollator;
}

function getFallbackCollator(): Intl.Collator | null {
  if (fallbackCollator !== undefined) return fallbackCollator;
  try {
    fallbackCollator = new Intl.Collator("zh-CN", {
      sensitivity: "base",
      numeric: true,
    });
  } catch {
    fallbackCollator = null;
  }
  return fallbackCollator;
}

function compareText(left: string, right: string): number {
  const primary = getPinyinCollator() || getFallbackCollator();
  if (primary) return primary.compare(left, right);
  return left < right ? -1 : left > right ? 1 : 0;
}

function compareOrdinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function asText(value: unknown): string {
  return String(value ?? "").trim();
}

function normalizedKind(value: unknown): string {
  const kind = asText(value).toLowerCase();
  return SUPPORTED_KINDS.has(kind) ? kind : "all";
}

function normalizedSort(value: unknown, fallback: string): string {
  const sort = asText(value);
  return SUPPORTED_SORTS.has(sort) ? sort : fallback;
}

function normalizedDirection(value: unknown, fallback: string): string {
  const direction = asText(value).toLowerCase();
  return SUPPORTED_DIRECTIONS.has(direction) ? direction : fallback;
}

export function createPluginViewState(value: Partial<PluginViewState> = {}, tab: PluginViewTab = "local"): PluginViewState {
  const defaults = DEFAULT_PLUGIN_VIEW_STATES[tab];
  return {
    query: asText(value.query),
    kind: normalizedKind(value.kind),
    sortBy: normalizedSort(value.sortBy, defaults.sortBy),
    direction: normalizedDirection(value.direction, defaults.direction),
  };
}

export function defaultPluginViewState(tab: PluginViewTab = "local"): PluginViewState {
  return { ...DEFAULT_PLUGIN_VIEW_STATES[tab] };
}

export function isPluginViewStateActive(value: Partial<PluginViewState>, tab: PluginViewTab = "local"): boolean {
  const state = createPluginViewState(value, tab);
  const defaults = DEFAULT_PLUGIN_VIEW_STATES[tab];
  return state.kind !== defaults.kind || state.sortBy !== defaults.sortBy || state.direction !== defaults.direction;
}

export function pluginSearchText(plugin: PluginViewRecord): string {
  const authors = Array.isArray(plugin?.authors)
    ? plugin.authors.map(author => author?.name || "")
    : [];
  const tags = Array.isArray(plugin?.tags) ? plugin.tags : [];
  return [
    plugin?.name,
    plugin?.displayName,
    plugin?.description,
    plugin?.gameName,
    plugin?.kind,
    ...authors,
    ...tags,
  ]
    .filter(Boolean)
    .join(" ")
    .toLocaleLowerCase("zh-CN");
}

function dateValue(value: unknown): number | null {
  const text = asText(value);
  if (!/^\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2}))?$/.test(text)) return null;
  const time = Date.parse(text);
  if (!Number.isFinite(time)) return null;
  if (text.length === 10 && new Date(time).toISOString().slice(0, 10) !== text) return null;
  return time;
}

function compareDate(left: PluginViewRecord, right: PluginViewRecord, field: string, direction: string): number {
  const leftValue = dateValue(left?.[field]);
  const rightValue = dateValue(right?.[field]);
  if (leftValue === null && rightValue === null) return 0;
  if (leftValue === null) return 1;
  if (rightValue === null) return -1;
  const result = leftValue - rightValue;
  return direction === "desc" ? -result : result;
}

function comparePlugins(left: PluginViewRecord, right: PluginViewRecord, state: PluginViewState): number {
  let result: number;
  if (state.sortBy === "name") {
    result = compareText(
      asText(left.displayName) || asText(left.name),
      asText(right.displayName) || asText(right.name),
    );
  } else {
    result = compareDate(left, right, state.sortBy, state.direction);
  }
  if (result !== 0) return state.sortBy === "name" ? (state.direction === "desc" ? -result : result) : result;

  const leftName = asText(left.name);
  const rightName = asText(right.name);
  result = compareOrdinal(leftName, rightName);
  return result;
}

interface PluginSearchIndex {
  raw: string[];
  romanized: Array<{ full: string; initials: string }>;
}

const searchIndexCache = new Map<string, PluginSearchIndex>();
const han = /\p{Script=Han}/u;

function normalizeText(value: string): string {
  return value.normalize("NFKC").toLocaleLowerCase("zh-CN");
}

function compactRoman(value: string): string {
  return normalizeText(value).normalize("NFD").replace(/\p{M}/gu, "")
    .replace(/ü/g, "v").replace(/[\s\-_.·'’]+/g, "");
}

function namesForSearch(plugin: PluginViewRecord): string[] {
  const locales = plugin.locales && typeof plugin.locales === "object" ? Object.values(plugin.locales) : [];
  return [plugin.name, plugin.displayName, plugin.gameName,
    ...locales.flatMap(locale => [locale?.displayName, locale?.gameName])]
    .filter((value): value is string => typeof value === "string" && value.trim().length > 0);
}

export function buildPluginSearchIndex(plugin: PluginViewRecord): PluginSearchIndex {
  const names = namesForSearch(plugin);
  const raw = [normalizeText(pluginSearchText(plugin)), ...names.map(normalizeText)];
  const cacheKey = JSON.stringify([raw, names]);
  const cached = searchIndexCache.get(cacheKey);
  if (cached) return cached;
  const romanized = names.filter(name => han.test(name)).map(name => {
    const syllables = pinyin(name, { type: "array", toneType: "none", nonZh: "consecutive" });
    return {
      full: compactRoman(syllables.join("")),
      initials: compactRoman(syllables.map(syllable => syllable[0] || "").join("")),
    };
  });
  const index = { raw, romanized };
  if (searchIndexCache.size >= 512) searchIndexCache.clear();
  searchIndexCache.set(cacheKey, index);
  return index;
}

export function matchesPluginQuery(plugin: PluginViewRecord, query: string): boolean {
  const normalized = normalizeText(query.trim());
  if (!normalized) return true;
  const index = buildPluginSearchIndex(plugin);
  if (index.raw.some(field => field.includes(normalized))) return true;
  if (han.test(normalized)) return false;
  const roman = compactRoman(normalized);
  return Boolean(roman) && index.romanized.some(name =>
    name.full.includes(roman) || name.initials.includes(roman));
}

export function filterAndSortPlugins<T extends PluginViewRecord>(plugins: T[] | unknown, value: Partial<PluginViewState> = {}, tab: PluginViewTab = "local"): T[] {
  const state = createPluginViewState(value, tab);
  const source = Array.isArray(plugins) ? (plugins as T[]) : [];
  return source
    .filter(plugin => state.kind === "all" || asText(plugin?.kind).toLowerCase() === state.kind)
    .filter(plugin => matchesPluginQuery(plugin, state.query))
    .map((plugin, index) => ({ plugin, index }))
    .sort((left, right) => comparePlugins(left.plugin, right.plugin, state) || left.index - right.index)
    .map(item => item.plugin);
}
