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
  if (status === "success") return `<span class="badge ok">${t("ui.success")}</span>`;
  if (status === "partial") return `<span class="badge warn">${t("ui.partially_failed")}</span>`;
  if (status === "running") return `<span class="badge blue">${t("ui.running")}</span>`;
  if (status === "cancelled") return `<span class="badge warn">${t("ui.cancelled")}</span>`;
  if (status === "skipped") return `<span class="badge blue">${t("ui.skipped")}</span>`;
  if (status === "error") return `<span class="badge bad">${t("ui.error")}</span>`;
  return `<span class="badge bad">${t("ui.failed")}</span>`;
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
      return t("ui.running");
    case "run.success":
      return fallback;
    case "run.partial":
      return fallback;
    case "run.cancelled":
      return fallback === "-" ? t("ui.run_cancelled") : fallback;
    case "run.daily_cap":
      return t("ui.the_daily_success_limit_was_reached_value_value_this_run_was_skipped", args);
    case "run.user_unavailable":
      return t("ui.user_value_does_not_exist_or_is_disabled", args);
    case "run.spec_failed":
      return fallback;
    case "run.config_selection_required":
      return t("ui.multiple_configurations_exist_in_the_script_directory_choose_one_in_edit_configuration_before_running");
    case "run.user_config_load_failed":
      return t("ui.failed_to_load_the_user_s_configuration_value", { reason });
    case "run.retry_prepare_failed":
      return t("ui.configuration_swap_before_retry_failed_value", { reason });
    case "run.max_attempts":
      return t("ui.the_maximum_number_of_attempts_value_failed_last_reason_value", {
        maximum: args.maximum || record?.maxAttempts || "-",
        reason,
      });
    case "run.script_missing":
      return t("ui.the_script_instance_does_not_exist_or_was_deleted");
    case "run.no_enabled_users":
      return t("ui.no_enabled_users_are_configured_for_this_script_instance_skipped");
    case "run.plugin_unavailable":
      return t("ui.the_bound_value_this_run_was_skipped", { reason });
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
    ? t("ui.specialized_plugin_value_is_not_installed_install_it_first", { name: status.displayName })
    : t("ui.specialized_plugin_value_is_unavailable_enable_it_first", { name: status.displayName });
  return t("ui.value_bound_to_script_instance_value", { name: script?.name || "", reason });
}

/** 脚本主程序图标加载失败时的通用占位图（内联 SVG，主题无关）。 */
export const scriptFallbackIcon = "data:image/svg+xml;charset=utf-8," + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" viewBox="0 0 40 40"><rect width="40" height="40" rx="9" fill="#000" opacity=".07"/><path d="M11 9h13l6 6v16H11z" fill="none" stroke="#000" stroke-opacity=".38" stroke-width="2" stroke-linejoin="round"/><path d="M24 9v6h6" fill="none" stroke="#000" stroke-opacity=".38" stroke-width="2" stroke-linejoin="round"/></svg>');
