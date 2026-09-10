import { getLocale, t } from "./i18n.js";

export function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, char => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[char]));
}

export function fmtTime(value) {
  if (!value) return "-";
  return new Date(value).toLocaleString(getLocale(), { hour12: false });
}

export function statusBadge(status) {
  if (status === "success") return `<span class="badge ok">${t("common.success")}</span>`;
  if (status === "partial") return `<span class="badge warn">${t("common.partially_failed")}</span>`;
  if (status === "running") return `<span class="badge blue">${t("common.running")}</span>`;
  if (status === "cancelled") return `<span class="badge warn">${t("common.cancelled")}</span>`;
  if (status === "skipped") return `<span class="badge blue">${t("common.skipped")}</span>`;
  if (status === "error") return `<span class="badge bad">${t("common.error")}</span>`;
  return `<span class="badge bad">${t("common.failed")}</span>`;
}

/**
 * 将持久化的稳定结果码投影为当前浏览器语言的说明。
 * ResultDetail 仍作为旧历史记录和未知结果码的回退，参数只使用稳定值。
 */
export function resultDetail(record) {
  const fallback = record?.resultDetail || "-";
  const args = record?.resultArgs || {};
  const reason = args.reason || record?.resultDetail || "-";
  switch (record?.resultCode) {
    case "run.running":
      return t("common.running");
    case "run.success":
      return fallback;
    case "run.partial":
      return fallback;
    case "run.cancelled":
      return fallback === "-" ? t("common.run_cancelled") : fallback;
    case "run.daily_cap":
      return t("common.status.daily_limit_skipped", args);
    case "run.user_unavailable":
      return t("common.error.user_unavailable", args);
    case "run.spec_failed":
      return fallback;
    case "run.config_selection_required":
      return t("common.configuration.multiple_select");
    case "run.user_config_load_failed":
      return t("common.error.user_config_load", { reason });
    case "run.retry_prepare_failed":
      return t("common.error.config_swap_failed", { reason });
    case "run.max_attempts":
      return t("common.error.attempts_exhausted", {
        maximum: args.maximum || record?.maxAttempts || "-",
        reason,
      });
    case "run.script_missing":
      return t("common.error.script_instance_deleted");
    case "run.no_enabled_users":
      return t("common.status.no_enabled_users");
    case "run.plugin_unavailable":
      return t("common.status.bound_skipped", { reason });
    case "run.host_error":
      return fallback;
    case "scheduler.trigger_failed":
      return fallback;
    default:
      return fallback;
  }
}

// 插件未安装或运行时元数据尚未返回时，使用插件目录中的正式展示名。
export const knownPluginDisplayNames = Object.freeze({
  bettergi: "BetterGI",
  maaend: "MaaEnd",
  march7th: "March7thAssistant",
  zzzonedragon: "ZenlessZoneZeroOneDragon",
});

export function pluginDisplayName(name, plugins = []) {
  const key = String(name || "").trim();
  const normalized = key.toLowerCase();
  const plugin = plugins.find(item => String(item?.name || "").trim().toLowerCase() === normalized);
  return knownPluginDisplayNames[normalized] || plugin?.displayName || plugin?.name || key;
}

/** 动态判断脚本实例的专项插件状态；插件卸载后仍可识别旧数据，但不再允许操作或运行。 */
export function scriptPluginStatus(script, plugins = []) {
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

export function scriptPluginUnavailableMessage(script, plugins = []) {
  const status = scriptPluginStatus(script, plugins);
  if (!status.specialized || status.available) return "";
  const reason = status.missing
    ? t("common.plugin.not_installed", { name: status.displayName })
    : t("common.plugin.unavailable", { name: status.displayName });
  return t("common.binding.status", { name: script?.name || "", reason });
}

/** 脚本主程序图标加载失败时的通用占位图（内联 SVG，主题无关）。 */
export const scriptFallbackIcon = "data:image/svg+xml;charset=utf-8," + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" viewBox="0 0 40 40"><rect width="40" height="40" rx="9" fill="#000" opacity=".07"/><path d="M11 9h13l6 6v16H11z" fill="none" stroke="#000" stroke-opacity=".38" stroke-width="2" stroke-linejoin="round"/><path d="M24 9v6h6" fill="none" stroke="#000" stroke-opacity=".38" stroke-width="2" stroke-linejoin="round"/></svg>');
