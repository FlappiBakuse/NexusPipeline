export function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, char => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[char]));
}

export function fmtTime(value) {
  if (!value) return "-";
  return new Date(value).toLocaleString("zh-CN", { hour12: false });
}

export function statusBadge(status) {
  if (status === "success") return '<span class="badge ok">成功</span>';
  if (status === "partial") return '<span class="badge warn">部分失败</span>';
  if (status === "running") return '<span class="badge blue">运行中</span>';
  if (status === "cancelled") return '<span class="badge warn">已取消</span>';
  if (status === "skipped") return '<span class="badge blue">已跳过</span>';
  if (status === "error") return '<span class="badge bad">异常</span>';
  return '<span class="badge bad">失败</span>';
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
    ? `专项插件「${status.displayName}」未安装，请先安装对应专项插件`
    : `专项插件「${status.displayName}」当前不可用，请先启用对应专项插件`;
  return `脚本实例「${script?.name || ""}」绑定的${reason}`;
}

/** 脚本主程序图标加载失败时的通用占位图（内联 SVG，主题无关）。 */
export const scriptFallbackIcon = "data:image/svg+xml;charset=utf-8," + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" viewBox="0 0 40 40"><rect width="40" height="40" rx="9" fill="#000" opacity=".07"/><path d="M11 9h13l6 6v16H11z" fill="none" stroke="#000" stroke-opacity=".38" stroke-width="2" stroke-linejoin="round"/><path d="M24 9v6h6" fill="none" stroke="#000" stroke-opacity=".38" stroke-width="2" stroke-linejoin="round"/></svg>');
