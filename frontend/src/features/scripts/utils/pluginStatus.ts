import { t } from "../../../platform/i18n";

export interface ScriptPluginRecord {
  name?: string;
  displayName?: string;
  kind?: string;
  state?: string;
  runtimeEnabled?: boolean;
}

export interface ScriptPluginStatus {
  specialized: boolean;
  available: boolean;
  missing: boolean;
  plugin: ScriptPluginRecord | null;
  displayName: string;
}

// 插件未安装或运行时元数据尚未返回时，使用插件目录中的正式展示名。
export const knownPluginDisplayNames: Readonly<Record<string, string>> = Object.freeze({
  bettergi: "BetterGI",
  maaend: "MaaEnd",
  march7th: "March7thAssistant",
  zzzonedragon: "ZenlessZoneZeroOneDragon",
});

export function pluginDisplayName(name: unknown, plugins: ScriptPluginRecord[] = []): string {
  const key = String(name || "").trim();
  const normalized = key.toLowerCase();
  const plugin = plugins.find(item => String(item?.name || "").trim().toLowerCase() === normalized);
  return knownPluginDisplayNames[normalized] || plugin?.displayName || plugin?.name || key;
}

/** 动态判断脚本实例的专项插件状态；插件卸载后仍可识别旧数据，但不再允许操作或运行。 */
export function scriptPluginStatus(script: Record<string, any> | null | undefined, plugins: ScriptPluginRecord[] = []): ScriptPluginStatus {
  const pluginType = String(script?.pluginType || "").trim();
  if (!pluginType) {
    return { specialized: false, available: true, missing: false, plugin: null, displayName: "" };
  }
  const normalized = pluginType.toLowerCase();
  const plugin = plugins.find(item => String(item?.name || "").trim().toLowerCase() === normalized) || null;
  const missing = !plugin || plugin.kind !== "data-specialized";
  const active = !!plugin
    && plugin.kind === "data-specialized"
    && plugin.runtimeEnabled !== false
    && (!plugin.state || plugin.state === "Active");
  return {
    specialized: true,
    available: !missing && active,
    missing,
    plugin,
    displayName: pluginDisplayName(pluginType, plugins),
  };
}

export function scriptPluginUnavailableMessage(script: Record<string, any> | null | undefined, plugins: ScriptPluginRecord[] = []): string {
  const status = scriptPluginStatus(script, plugins);
  if (!status.specialized || status.available) return "";
  const reason = status.missing
    ? t("common.plugin.not_installed", { name: status.displayName })
    : t("common.plugin.unavailable", { name: status.displayName });
  return t("common.binding.status", { name: script?.name || "", reason });
}
