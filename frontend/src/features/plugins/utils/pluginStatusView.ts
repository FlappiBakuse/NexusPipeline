/** 插件浏览的展示投影：类型标签、运行态、商店状态与商店动作。
 *  页面、列表与详情共用同一投影，避免三处各自实现。 */

export interface PluginViewPlugin {
  name?: string;
  displayName?: string;
  description?: string;
  kind?: string;
  version?: string;
  state?: string;
  runtimeEnabled?: boolean;
  configuredEnabled?: boolean;
  status?: string;
  installed?: boolean;
  installedName?: string;
  installedVersion?: string;
  updateAvailable?: boolean;
  pendingAction?: string;
  pendingVersion?: string;
  compatible?: boolean;
  authors?: Array<{ name?: string }>;
  tags?: string[];
  [key: string]: unknown;
}

export type PluginViewTab = "local" | "store";

export interface PluginStoreAction {
  action: string;
  label: string;
  tone: string;
}

export interface PluginStatusView {
  label: string;
  tone: "ok" | "warn" | "bad" | "muted";
}

export type PluginTranslator = (key: string, args?: Record<string, unknown>, fallback?: string) => string;

export function pluginKindLabel(plugin: PluginViewPlugin, t: PluginTranslator) {
  return t(plugin.kind === "data-specialized" ? "common.specialized_plugin" : "plugins.general_plugin");
}

export function pluginKindTone(plugin: PluginViewPlugin): "muted" | "blue" {
  return plugin.kind === "data-specialized" ? "blue" : "muted";
}

export function runtimeLabel(plugin: PluginViewPlugin, t: PluginTranslator) {
  if ((plugin.state || (plugin.runtimeEnabled ? "Active" : "Disabled")) === "Active") {
    return plugin.configuredEnabled ? t("common.running") : t("plugins.running_restart_required");
  }
  if (plugin.state === "InitFailed") return t("plugins.initialization_failed");
  if (plugin.state === "Incompatible") return t("plugins.incompatible_api");
  return plugin.configuredEnabled ? t("plugins.restart_required") : t("common.disabled");
}

export function runtimeTone(plugin: PluginViewPlugin): "ok" | "bad" | "muted" {
  if ((plugin.state || (plugin.runtimeEnabled ? "Active" : "Disabled")) === "Active") return "ok";
  if (["InitFailed", "InitTimedOut", "StartTimedOut", "StopTimedOut", "Incompatible"].includes(String(plugin.state))) return "bad";
  return "muted";
}

export function storeStatusLabel(plugin: PluginViewPlugin, t: PluginTranslator) {
  const keys: Record<string, string> = {
    "not-installed": "plugins.not_installed",
    installed: "plugins.installed",
    "update-available": "plugins.update_available",
    pending: "plugins.restart_required",
    incompatible: "plugins.incompatible_with_host",
    unlisted: "plugins.not_listed_in_repository",
  };
  return t(keys[String(plugin.status)] || "plugins.available");
}

export function storeTone(plugin: PluginViewPlugin): "ok" | "warn" | "bad" | "muted" {
  if (plugin.status === "incompatible") return "bad";
  if (["update-available", "pending"].includes(String(plugin.status))) return "warn";
  if (plugin.status === "installed") return "ok";
  return "muted";
}

export function storeActionNotice(plugin: PluginViewPlugin, t: PluginTranslator) {
  if (plugin.status === "pending") {
    return t("plugins.store.pending_restart", {
      action: t(plugin.pendingAction === "uninstall" ? "plugins.uninstall" : "plugins.install"),
      version: plugin.pendingVersion || plugin.version || "",
      restart: t("plugins.effective_after_restart"),
    });
  }
  if (plugin.compatible === false) return t("plugin.store.incompatible", {}, "The current host version is incompatible");
  return "";
}

export function storeActions(plugin: PluginViewPlugin, t: PluginTranslator): PluginStoreAction[] {
  if (storeActionNotice(plugin, t)) return [];
  const result: PluginStoreAction[] = [];
  if (plugin.status === "unlisted") {
    result.push({ action: "uninstall", label: t("plugins.uninstall_plugin"), tone: "danger" });
    return result;
  }
  if (!plugin.installed) {
    result.push({ action: "install", label: t("plugins.install_plugin"), tone: "primary" });
  } else if (plugin.updateAvailable) {
    result.push({ action: "update", label: t("plugins.update_plugin"), tone: "primary" });
  }
  if (plugin.installed && (!plugin.installedName || plugin.installedName === plugin.name)) {
    result.push({ action: "uninstall", label: t("plugins.uninstall_plugin"), tone: "tertiary" });
  }
  return result;
}

export function pluginStatusView(plugin: PluginViewPlugin, tab: PluginViewTab, t: PluginTranslator): PluginStatusView {
  return tab === "store"
    ? { label: storeStatusLabel(plugin, t), tone: storeTone(plugin) }
    : { label: runtimeLabel(plugin, t), tone: runtimeTone(plugin) };
}

export function authorName(plugin: PluginViewPlugin, t: PluginTranslator) {
  return Array.isArray(plugin.authors) && plugin.authors.length
    ? plugin.authors.map(author => author?.name || t("plugins.unknown_author")).join(", ")
    : t("plugins.not_provided");
}

export function pluginTags(plugin: PluginViewPlugin): string[] {
  return Array.isArray(plugin.tags) ? plugin.tags.map(String).filter(Boolean) : [];
}
