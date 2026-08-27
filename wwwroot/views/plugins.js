import { api } from "../core/api.js";
import { esc } from "../core/format.js";
import { pageHeader } from "../core/forms.js";
import { isCurrent, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { markRestartRequired } from "./settings.js";

let activeTab = "store";

function groupFor(plugin) {
  if (plugin.kind === "managed-code") return "代码插件";
  if (plugin.kind === "data-specialized") return "数据化专项插件";
  return "其他插件";
}

function runtimeLabel(plugin) {
  const runtimeState = plugin.state || (plugin.runtimeEnabled ? "Active" : "Disabled");
  if (runtimeState === "Active") return plugin.configuredEnabled ? "运行中" : "运行中 · 待重启";
  if (runtimeState === "InitFailed") return "初始化失败";
  if (runtimeState === "Incompatible") return "API 不兼容";
  if (runtimeState === "Loading") return "加载中";
  return plugin.configuredEnabled ? "待重启" : "已禁用";
}

function runtimeClass(plugin) {
  const runtimeState = plugin.state || (plugin.runtimeEnabled ? "Active" : "Disabled");
  if (runtimeState === "Active") return "ok";
  if (runtimeState === "InitFailed" || runtimeState === "Incompatible") return "danger";
  return "muted";
}

function pluginRow(plugin) {
  const apiLabel = plugin.apiVersion ? ` · API v${esc(plugin.apiVersion)}` : "";
  return `<article class="plugin-row"><div class="plugin-row-main"><strong class="plugin-name-scroll" tabindex="0" title="${esc(plugin.displayName)}"><span class="plugin-name-scroll-inner">${esc(plugin.displayName)}</span></strong><span class="muted">${esc(plugin.description || "本地扩展能力")} · ${esc(plugin.version || "未标注")}${apiLabel}</span>${plugin.error ? `<span class="field-error-message">${esc(plugin.error)}</span>` : ""}</div><span class="badge ${runtimeClass(plugin)}" data-testid="plugin-status">${runtimeLabel(plugin)}</span><div class="plugin-row-action row-actions"><button class="tertiary" type="button" data-action="toggle-plugin" data-name="${esc(plugin.name)}" data-enabled="${!plugin.configuredEnabled}">${plugin.configuredEnabled ? "禁用" : "启用"}</button></div></article>`;
}

function pluginTabs() {
  const storeClass = activeTab === "store" ? "primary" : "tertiary";
  const localClass = activeTab === "local" ? "primary" : "tertiary";
  return '<div class="plugin-tabs" role="tablist" aria-label="插件视图">' +
    '<button type="button" class="' + storeClass + '" role="tab" aria-selected="' + String(activeTab === "store") + '" data-action="switch-plugin-tab" data-tab="store" data-testid="plugin-store-tab">插件仓库</button>' +
    '<button type="button" class="' + localClass + '" role="tab" aria-selected="' + String(activeTab === "local") + '" data-action="switch-plugin-tab" data-tab="local" data-testid="plugin-local-tab">本地插件</button>' +
    '</div>';
}

function localPluginContent(status) {
  const plugins = status.plugins || [];
  const groups = [
    { label: "数据化专项插件", items: plugins.filter(plugin => plugin.kind === "data-specialized") },
    { label: "代码插件", items: plugins.filter(plugin => plugin.kind === "managed-code") },
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
    pending: "待重启",
    incompatible: "宿主不兼容",
  }[plugin.status] || "可用";
}

function storeStatusClass(plugin) {
  if (plugin.status === "incompatible") return "danger";
  if (plugin.status === "update-available" || plugin.status === "pending") return "warning";
  if (plugin.status === "installed") return "ok";
  return "muted";
}

function storePluginRow(plugin) {
  const pending = plugin.status === "pending";
  const incompatible = !plugin.compatible;
  let actions = "";
  if (!pending && !incompatible) {
    if (!plugin.installed) {
      actions += '<button class="primary" type="button" data-action="store-install" data-name="' + esc(plugin.name) + '" data-testid="plugin-install-' + esc(plugin.name) + '">安装</button>';
    } else if (plugin.updateAvailable) {
      actions += '<button class="primary" type="button" data-action="store-update" data-name="' + esc(plugin.name) + '" data-testid="plugin-update-' + esc(plugin.name) + '">更新</button>';
    }
    if (plugin.installed) {
      actions += '<button class="tertiary" type="button" data-action="store-uninstall" data-name="' + esc(plugin.name) + '" data-testid="plugin-uninstall-' + esc(plugin.name) + '">卸载</button>';
    }
  }
  if (pending) {
    actions = '<span class="muted">' + esc(plugin.pendingAction === "uninstall" ? "卸载" : "安装") + " v" + esc(plugin.pendingVersion || plugin.version) + "，重启后生效</span>";
  }
  if (incompatible) {
    actions = '<span class="muted">' + esc(plugin.compatibilityReason || "当前宿主版本不兼容") + "</span>";
  }
  const installedLabel = plugin.installed ? " · 当前 v" + esc(plugin.installedVersion) : "";
  const apiLabel = plugin.apiVersion ? " · API v" + esc(plugin.apiVersion) : "";
  return '<article class="plugin-store-row" data-testid="plugin-store-row"><div class="plugin-row-main"><strong class="plugin-name-scroll" tabindex="0" title="' + esc(plugin.displayName) + '"><span class="plugin-name-scroll-inner">' + esc(plugin.displayName) + '</span></strong><span class="muted">' + esc(plugin.description || "官方插件") + " · v" + esc(plugin.version) + installedLabel + apiLabel + "</span>" + (plugin.gameName ? '<span class="muted">' + esc(plugin.gameName) + "</span>" : "") + '</div><span class="badge ' + storeStatusClass(plugin) + '" data-testid="plugin-store-status">' + storeStatusLabel(plugin) + '</span><div class="plugin-store-actions row-actions">' + actions + "</div></article>";
}

function storePluginContent(data) {
  if (!data) return '<div class="empty"><strong>插件仓库加载中</strong></div>';
  if (!data.available) {
    return '<section class="plugins-table" data-testid="plugin-store-list"><div class="empty"><strong>插件仓库暂不可用</strong><span>' + esc(data.error || "请检查网络连接或代理设置。") + '</span><button class="tertiary" type="button" data-action="store-refresh" data-testid="plugin-store-refresh">重新加载</button></div></section>';
  }
  const warning = data.stale ? '<div class="callout callout-warning" data-testid="plugin-store-stale">仓库连接暂时不可用，当前显示缓存目录。' + (data.error ? " " + esc(data.error) : "") + "</div>" : "";
  const plugins = (data.plugins || []).map(storePluginRow).join("");
  return warning + '<section class="plugins-table plugin-store-list" data-testid="plugin-store-list">' + (plugins || '<div class="empty"><strong>暂无可用插件</strong></div>') + '</section><div class="plugin-store-footer"><span class="muted">仓库目录更新时间：' + esc(data.fetchedAt || "未知") + '</span><button class="tertiary" type="button" data-action="store-refresh" data-testid="plugin-store-refresh">刷新仓库</button></div><p class="muted helper-copy plugin-helper">安装、更新和卸载会在重启服务后生效。</p>';
}

export async function pagePlugins(token) {
  if (!isCurrent("plugins", token)) return;
  navActive("plugins"); setTopbarTitle("插件");
  let content;
  if (activeTab === "store") {
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
  if (!isCurrent("plugins", token)) return;
  render(pageHeader(
    "插件",
    "插件",
    activeTab === "store" ? "浏览官方插件并管理安装版本。" : "查看已安装插件并管理运行状态。",
    pluginTabs(),
  ) + content);
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

export const actions = {
  "toggle-plugin": target => togglePlugin(target.dataset.name, target.dataset.enabled === "true"),
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
