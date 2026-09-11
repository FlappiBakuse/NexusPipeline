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
  [key: string]: unknown;
}

const DEFAULT_PLUGIN_VIEW_STATE: PluginViewState = Object.freeze({
  query: "",
  kind: "all",
  sortBy: "name",
  direction: "asc",
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

function normalizedSort(value: unknown): string {
  const sort = asText(value);
  return SUPPORTED_SORTS.has(sort) ? sort : "name";
}

function normalizedDirection(value: unknown): string {
  const direction = asText(value).toLowerCase();
  return SUPPORTED_DIRECTIONS.has(direction) ? direction : "asc";
}

export function createPluginViewState(value: Partial<PluginViewState> = {}): PluginViewState {
  return {
    query: asText(value.query),
    kind: normalizedKind(value.kind),
    sortBy: normalizedSort(value.sortBy),
    direction: normalizedDirection(value.direction),
  };
}

export function defaultPluginViewState(): PluginViewState {
  return { ...DEFAULT_PLUGIN_VIEW_STATE };
}

export function isPluginViewStateActive(value: Partial<PluginViewState>): boolean {
  const state = createPluginViewState(value);
  return state.kind !== "all" || state.sortBy !== "name" || state.direction !== "asc";
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

function compareDate(left: PluginViewRecord, right: PluginViewRecord, field: string, direction: string): number {
  const leftValue = asText(left?.[field]);
  const rightValue = asText(right?.[field]);
  if (!leftValue && !rightValue) return 0;
  if (!leftValue) return 1;
  if (!rightValue) return -1;
  const result = compareOrdinal(leftValue, rightValue);
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
  if (result !== 0 && state.sortBy !== "name" && state.direction === "desc") return -result;
  return result;
}

export function filterAndSortPlugins<T extends PluginViewRecord>(plugins: T[] | unknown, value: Partial<PluginViewState> = {}): T[] {
  const state = createPluginViewState(value);
  const query = state.query.toLocaleLowerCase("zh-CN");
  const source = Array.isArray(plugins) ? (plugins as T[]) : [];
  return source
    .filter(plugin => state.kind === "all" || asText(plugin?.kind).toLowerCase() === state.kind)
    .filter(plugin => !query || pluginSearchText(plugin).includes(query))
    .map((plugin, index) => ({ plugin, index }))
    .sort((left, right) => comparePlugins(left.plugin, right.plugin, state) || left.index - right.index)
    .map(item => item.plugin);
}
