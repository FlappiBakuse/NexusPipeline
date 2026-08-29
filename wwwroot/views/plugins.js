import { api } from "../core/api.js";
import { esc } from "../core/format.js";
import { pageHeader } from "../core/forms.js";
import { isCurrent, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { refreshPluginRuntime } from "../core/plugin-runtime.js";
import { markRestartRequired } from "./settings.js";

let activeTab = "local";
let pluginLoadId = 0;

function pluginKindLabel(plugin) {
  return plugin.kind === "data-specialized" ? "专项插件" : "通用插件";
}

function pluginKindClass(plugin) {
  return plugin.kind === "data-specialized" ? "blue" : "muted";
}

function pluginKindBadge(plugin) {
  return `<span class="badge ${pluginKindClass(plugin)} plugin-kind-badge">${pluginKindLabel(plugin)}</span>`;
}

function pluginNameMarkup(plugin) {
  const displayName = esc(plugin.displayName || plugin.name);
  return `<div class="plugin-name-line"><strong class="plugin-name-scroll" tabindex="0" title="${displayName}"><span class="plugin-name-scroll-inner">${displayName}</span></strong></div>`;
}

function pluginDetailsMarkup(parts) {
  return parts.filter(value => value !== undefined && value !== null && value !== "").map(esc).join(" · ");
}

function changelogMarkup(plugin) {
  const entries = Array.isArray(plugin.changelog) ? plugin.changelog.slice(0, 3) : [];
  if (!entries.length) return "";
  const body = entries.map(entry => {
    const items = Array.isArray(entry.items) ? entry.items : [];
    return `<section class="plugin-changelog-entry"><div class="plugin-changelog-version"><strong>v${esc(entry.version)}</strong><span class="muted">${esc(entry.date)}</span></div><ul>${items.map(item => `<li>${esc(item)}</li>`).join("")}</ul></section>`;
  }).join("");
  return `<details class="plugin-changelog"><summary>更新记录</summary><div class="plugin-changelog-body">${body}</div></details>`;
}

function pluginBadgesMarkup(plugin, statusMarkup) {
  const frontend = plugin.hasFrontend
    ? `<span class="badge ${plugin.frontendTrusted ? "ok" : "warn"}" title="可信前端模块可在同源管理页面运行 JavaScript/CSS">前端 ${plugin.frontendTrusted ? "已信任" : "待确认"}</span>`
    : "";
  return `<div class="plugin-row-badges">${pluginKindBadge(plugin)}${statusMarkup}${frontend}</div>`;
}

function runtimeLabel(plugin) {
  const runtimeState = plugin.state || (plugin.runtimeEnabled ? "Active" : "Disabled");
  if (runtimeState === "Active") return plugin.configuredEnabled ? "运行中" : "运行中 · 待重启";
  if (runtimeState === "InitFailed") return "初始化失败";
  if (runtimeState === "InitTimedOut") return "初始化超时";
  if (runtimeState === "StartTimedOut") return "启动超时";
  if (runtimeState === "StopTimedOut") return "停止超时";
  if (runtimeState === "Incompatible") return "API 不兼容";
  if (runtimeState === "Loading") return "加载中";
  return plugin.configuredEnabled ? "待重启" : "已禁用";
}

function runtimeClass(plugin) {
  const runtimeState = plugin.state || (plugin.runtimeEnabled ? "Active" : "Disabled");
  if (runtimeState === "Active") return "ok";
  if (["InitFailed", "InitTimedOut", "StartTimedOut", "StopTimedOut", "Incompatible"].includes(runtimeState)) return "danger";
  return "muted";
}

function pluginRow(plugin) {
  const details = pluginDetailsMarkup([
    plugin.description || "本地扩展能力",
    plugin.gameName,
    plugin.version || "未标注",
    plugin.apiVersion ? `API v${plugin.apiVersion}` : "",
  ]);
  const status = `<span class="badge ${runtimeClass(plugin)}" data-testid="plugin-status">${runtimeLabel(plugin)}</span>`;
  const frontendAction = plugin.hasFrontend
    ? `<button class="${plugin.frontendTrusted ? "ghost" : "tertiary"}" type="button" data-action="toggle-plugin-frontend" data-name="${esc(plugin.name)}" data-trusted="${plugin.frontendTrusted ? "false" : "true"}" title="${plugin.frontendTrusted ? "撤销后该插件前端将在下次加载时停止" : "确认后该插件前端可在同源管理页面运行 JavaScript/CSS"}">${plugin.frontendTrusted ? "撤销前端信任" : "信任前端"}</button>`
    : "";
  return `<article class="plugin-row"><div class="plugin-row-main">${pluginNameMarkup(plugin)}<span class="muted plugin-description">${details}</span>${plugin.error ? `<span class="field-error-message">${esc(plugin.error)}</span>` : ""}</div>${pluginBadgesMarkup(plugin, status)}<div class="plugin-row-action row-actions">${frontendAction}<button class="tertiary" type="button" data-action="toggle-plugin" data-name="${esc(plugin.name)}" data-enabled="${!plugin.configuredEnabled}">${plugin.configuredEnabled ? "禁用" : "启用"}</button></div></article>`;
}

function pluginTabs(tab = activeTab) {
  const storeClass = tab === "store" ? "primary" : "tertiary";
  const localClass = tab === "local" ? "primary" : "tertiary";
  return '<div class="plugin-tabs" role="tablist" aria-label="插件视图">' +
    '<button type="button" class="' + localClass + '" role="tab" aria-selected="' + String(tab === "local") + '" data-action="switch-plugin-tab" data-tab="local" data-testid="plugin-local-tab">本地插件</button>' +
    '<button type="button" class="' + storeClass + '" role="tab" aria-selected="' + String(tab === "store") + '" data-action="switch-plugin-tab" data-tab="store" data-testid="plugin-store-tab">插件仓库</button>' +
    '</div>';
}

function pluginLoadingContent(title, message, testId) {
  return `<section class="plugins-table plugin-loading-state" data-testid="${testId}" role="status" aria-live="polite" aria-busy="true"><div class="plugin-loading-progress" role="progressbar" aria-label="${esc(title)}" aria-valuetext="正在获取目录"><span></span></div><strong>${esc(title)}</strong><span class="muted">${esc(message)}</span></section>`;
}

function localPluginContent(status) {
  const plugins = status.plugins || [];
  const groups = [
    { label: "通用插件", items: plugins.filter(plugin => plugin.kind === "managed-code") },
    { label: "专项插件", items: plugins.filter(plugin => plugin.kind === "data-specialized") },
    { label: "其他插件", items: plugins.filter(plugin => !["data-specialized", "managed-code"].includes(plugin.kind)) },
  ].filter(group => group.items.length);
  const groupMarkup = groups.map(group => '<section class="plugin-group"><div class="plugin-group-heading"><h3>' + group.label + "</h3><span>" + group.items.length + " 项</span></div>" + group.items.map(pluginRow).join("") + "</section>").join("");
  return '<section class="plugins-table plugin-groups" data-testid="plugins-list">' +
    (groupMarkup || '<div class="empty"><strong>暂无本地插件</strong><span>从插件仓库安装插件后，重启服务即可加载。</span></div>') +
    '</section><p class="muted helper-copy plugin-helper">本地插件状态变化会在服务重启后生效。通知渠道请在「设置」页配置。</p>';
}

function storeStatusLabel(plugin) {
  return {
    "not-installed": "未安装",
    installed: "已安装",
    "update-available": "有更新",
    "replacement-available": "可替换旧插件",
    pending: "待重启",
    "replacement-conflict": "替换冲突",
    "layout-conflict": "目录冲突",
    incompatible: "宿主不兼容",
    unlisted: "未列入仓库",
  }[plugin.status] || "可用";
}

function storeStatusClass(plugin) {
  if (plugin.status === "incompatible") return "danger";
  if (plugin.status === "replacement-conflict") return "danger";
  if (plugin.status === "layout-conflict") return "danger";
  if (plugin.status === "update-available" || plugin.status === "replacement-available" || plugin.status === "pending") return "warning";
  if (plugin.status === "installed") return "ok";
  return "muted";
}

function storePluginRow(plugin) {
  const pending = plugin.status === "pending";
  const incompatible = !plugin.compatible;
  const conflicted = plugin.status === "replacement-conflict";
  const layoutConflict = plugin.status === "layout-conflict";
  let actions = "";
  if (!pending && !incompatible && !conflicted) {
    if (plugin.status === "unlisted") {
      actions += '<button class="tertiary" type="button" data-action="store-uninstall" data-name="' + esc(plugin.name) + '">卸载</button>';
    } else if (!plugin.installed) {
      actions += '<button class="primary" type="button" data-action="store-install" data-name="' + esc(plugin.name) + '" data-testid="plugin-install-' + esc(plugin.name) + '">安装</button>';
    } else if (plugin.updateAvailable) {
      actions += '<button class="primary" type="button" data-action="store-update" data-name="' + esc(plugin.name) + '" data-testid="plugin-update-' + esc(plugin.name) + '">' + (plugin.status === "replacement-available" ? "替换旧插件" : "更新") + '</button>';
    }
    if (plugin.status !== "unlisted" && plugin.installed && (!plugin.installedName || plugin.installedName === plugin.name)) {
      actions += '<button class="tertiary" type="button" data-action="store-uninstall" data-name="' + esc(plugin.name) + '" data-testid="plugin-uninstall-' + esc(plugin.name) + '">卸载</button>';
    }
  }
  if (pending) {
    actions = '<span class="muted">' + esc(plugin.pendingAction === "uninstall" ? "卸载" : "安装") + " v" + esc(plugin.pendingVersion || plugin.version) + "，重启后生效</span>";
  }
  if (incompatible) {
    actions = '<span class="muted">' + esc(plugin.compatibilityReason || "当前宿主版本不兼容") + "</span>";
  }
  if (conflicted) {
    actions = '<span class="muted">目标插件与旧插件同时存在，请先处理本地插件冲突。</span>';
  }
  if (layoutConflict) {
    actions = '<span class="muted">物理目录存在冲突，请先处理本地插件目录。</span>';
  }
  const details = pluginDetailsMarkup([
    plugin.description || "官方插件",
    plugin.gameName,
    plugin.version ? `v${plugin.version}` : "",
    plugin.installed ? `当前 v${plugin.installedVersion}${plugin.installedName && plugin.installedName !== plugin.name ? `（${plugin.installedName}）` : ""}` : "",
    plugin.apiVersion ? `API v${plugin.apiVersion}` : "",
  ]);
  const status = '<span class="badge ' + storeStatusClass(plugin) + '" data-testid="plugin-store-status">' + storeStatusLabel(plugin) + '</span>';
  return '<article class="plugin-row plugin-store-row" data-testid="plugin-store-row"><div class="plugin-row-main">' + pluginNameMarkup(plugin) + '<span class="muted plugin-description">' + details + '</span>' + changelogMarkup(plugin) + '</div>' + pluginBadgesMarkup(plugin, status) + '<div class="plugin-row-action plugin-store-actions row-actions">' + actions + "</div></article>";
}

function storePluginContent(data) {
  if (!data) return pluginLoadingContent("正在加载插件仓库", "正在从官方仓库获取插件目录，请稍候…", "plugin-store-loading");
  if (!data.available) {
    return '<section class="plugins-table" data-testid="plugin-store-list"><div class="empty"><strong>插件仓库暂不可用</strong><span>' + esc(data.error || "请检查网络连接或代理设置。") + '</span><button class="tertiary" type="button" data-action="store-refresh" data-testid="plugin-store-refresh">重新加载</button></div></section>';
  }
  const warning = data.stale ? '<div class="callout callout-warning" data-testid="plugin-store-stale">仓库连接暂时不可用，当前显示缓存目录。' + (data.error ? " " + esc(data.error) : "") + "</div>" : "";
  const plugins = data.plugins || [];
  const groups = [
    { label: "通用插件", items: plugins.filter(plugin => plugin.kind !== "data-specialized") },
    { label: "专项插件", items: plugins.filter(plugin => plugin.kind === "data-specialized") },
  ].filter(group => group.items.length);
  const groupMarkup = groups.map(group => '<section class="plugin-group"><div class="plugin-group-heading"><h3>' + group.label + "</h3><span>" + group.items.length + " 项</span></div>" + group.items.map(storePluginRow).join("") + "</section>").join("");
  return warning + '<section class="plugins-table plugin-groups plugin-store-groups" data-testid="plugin-store-list">' + (groupMarkup || '<div class="empty"><strong>暂无可用插件</strong></div>') + '</section><div class="plugin-store-footer"><span class="muted">仓库目录更新时间：' + esc(data.fetchedAt || "未知") + '</span><button class="tertiary" type="button" data-action="store-refresh" data-testid="plugin-store-refresh">刷新仓库</button></div><p class="muted helper-copy plugin-helper">安装、更新和卸载会在重启服务后生效。</p>';
}

function pluginPageMarkup(tab, content) {
  return pageHeader(
    "插件",
    "插件",
    tab === "store" ? "浏览官方插件并管理安装版本。" : "查看已安装插件并管理运行状态。",
    pluginTabs(tab),
  ) + content;
}

export async function pagePlugins(token) {
  if (!isCurrent("plugins", token)) return;
  navActive("plugins"); setTopbarTitle("插件");
  const requestedTab = activeTab;
  const loadId = ++pluginLoadId;
  render(pluginPageMarkup(
    requestedTab,
    requestedTab === "store"
      ? storePluginContent(null)
      : pluginLoadingContent("正在加载本地插件", "正在读取本机插件状态，请稍候…", "plugin-local-loading"),
  ));
  let content;
  if (requestedTab === "store") {
    try {
      content = storePluginContent(await api("GET", "/api/plugins/store"));
    } catch (error) {
      content = storePluginContent({ available: false, error: error.message });
    }
  } else {
    try {
      content = localPluginContent(await api("GET", "/api/status"));
    } catch (error) {
      content = '<div class="empty"><strong>加载本地插件失败</strong><span>' + esc(error.message) + "</span></div>";
    }
  }
  if (!isCurrent("plugins", token) || loadId !== pluginLoadId || activeTab !== requestedTab) return;
  render(pluginPageMarkup(requestedTab, content));
}

export async function togglePlugin(name, enabled) {
  try {
    await api("POST", "/api/plugins/" + name + "/" + (enabled ? "enable" : "disable"));
    markRestartRequired();
    toast("已更新（重启生效）");
    await pagePlugins(state.routeToken);
  } catch (error) {
    toast(error.message, "error");
  }
}

async function runStoreAction(name, action) {
  try {
    await api("POST", "/api/plugins/store/" + encodeURIComponent(name) + "/" + action);
    markRestartRequired();
    toast(action === "uninstall" ? "已登记卸载（重启生效）" : "已登记操作（重启生效）");
    await pagePlugins(state.routeToken);
  } catch (error) {
    toast(error.message, "error");
  }
}

async function togglePluginFrontend(name, trusted) {
  try {
    await api("POST", `/api/plugins/${encodeURIComponent(name)}/${trusted ? "trust-frontend" : "revoke-frontend"}`);
    toast(trusted ? "已信任插件前端" : "已撤销插件前端信任");
    if (trusted) await refreshPluginRuntime();
    await pagePlugins(state.routeToken);
  } catch (error) {
    toast(error.message, "error");
  }
}

export const actions = {
  "toggle-plugin": target => togglePlugin(target.dataset.name, target.dataset.enabled === "true"),
  "toggle-plugin-frontend": target => withBusy(target, () => togglePluginFrontend(target.dataset.name, target.dataset.trusted === "true")),
  "switch-plugin-tab": target => {
    activeTab = target.dataset.tab === "local" ? "local" : "store";
    pagePlugins(state.routeToken);
  },
  "store-refresh": target => withBusy(target, async () => {
    try {
      await api("POST", "/api/plugins/store/refresh");
      toast("插件仓库已刷新");
      await pagePlugins(state.routeToken);
    } catch (error) {
      toast(error.message, "error");
    }
  }),
  "store-install": target => withBusy(target, () => runStoreAction(target.dataset.name, "install")),
  "store-update": target => withBusy(target, () => runStoreAction(target.dataset.name, "update")),
  "store-uninstall": target => withBusy(target, () => runStoreAction(target.dataset.name, "uninstall")),
};
