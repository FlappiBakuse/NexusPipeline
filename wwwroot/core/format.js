import { getLocale, text, translateText } from "./i18n.js";

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
  if (status === "success") return `<span class="badge ok">${text("成功")}</span>`;
  if (status === "partial") return `<span class="badge warn">${text("部分失败")}</span>`;
  if (status === "running") return `<span class="badge blue">${text("运行中")}</span>`;
  if (status === "cancelled") return `<span class="badge warn">${text("已取消")}</span>`;
  if (status === "skipped") return `<span class="badge blue">${text("已跳过")}</span>`;
  if (status === "error") return `<span class="badge bad">${text("异常")}</span>`;
  return `<span class="badge bad">${text("失败")}</span>`;
}

/**
 * 将持久化的稳定结果码投影为当前浏览器语言的说明。
 * ResultDetail 仍作为旧历史记录和未知结果码的回退，参数只使用稳定值。
 */
export function resultDetail(record) {
  const fallback = translateText(record?.resultDetail || "-");
  const args = record?.resultArgs || {};
  const reason = translateText(args.reason || record?.resultDetail || "-");
  switch (record?.resultCode) {
    case "run.running":
      return text("运行中");
    case "run.success":
      return fallback;
    case "run.partial":
      return fallback;
    case "run.cancelled":
      return fallback === "-" ? text("运行已取消") : fallback;
    case "run.daily_cap":
      return text("当天已成功运行 {successful}/{maximum} 次，达到最多成功运行次数，已跳过本次运行", args);
    case "run.user_unavailable":
      return text("用户「{user}」不存在或已禁用", args);
    case "run.spec_failed":
      return fallback;
    case "run.config_selection_required":
      return text("当前脚本目录存在多个配置，请先编辑配置选择要接管的配置");
    case "run.user_config_load_failed":
      return text("用户配置加载失败：{reason}", { reason });
    case "run.retry_prepare_failed":
      return text("重试前配置交换失败：{reason}", { reason });
    case "run.max_attempts":
      return text("达到最大尝试次数（{maximum} 次）仍失败，最后原因：{reason}", {
        maximum: args.maximum || record?.maxAttempts || "-",
        reason,
      });
    case "run.script_missing":
      return text("脚本实例不存在或已被删除");
    case "run.no_enabled_users":
      return text("脚本实例未配置启用用户，已跳过");
    case "run.plugin_unavailable":
      return text("绑定的{reason}，已跳过本次运行", { reason });
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
    ? text("专项插件「{name}」未安装，请先安装对应专项插件", { name: status.displayName })
    : text("专项插件「{name}」当前不可用，请先启用对应专项插件", { name: status.displayName });
  return text("脚本实例「{name}」绑定的{reason}", { name: script?.name || "", reason });
}

/** 脚本主程序图标加载失败时的通用占位图（内联 SVG，主题无关）。 */
export const scriptFallbackIcon = "data:image/svg+xml;charset=utf-8," + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" viewBox="0 0 40 40"><rect width="40" height="40" rx="9" fill="#000" opacity=".07"/><path d="M11 9h13l6 6v16H11z" fill="none" stroke="#000" stroke-opacity=".38" stroke-width="2" stroke-linejoin="round"/><path d="M24 9v6h6" fill="none" stroke="#000" stroke-opacity=".38" stroke-width="2" stroke-linejoin="round"/></svg>');
