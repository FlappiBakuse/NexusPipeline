import { api } from "../core/api.js";
import { esc } from "../core/format.js";
import { pageHeader } from "../core/forms.js";
import { renderMarkdown } from "../core/markdown.js";
import { isCurrent, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { markRestartRequired } from "./settings.js";

let activeTab = "local";
let pluginLoadId = 0;
let detailLoadId = 0;
let searchQuery = "";
let detailVisibleMobile = false;

const selectedByTab = { local: "", store: "" };
const listState = {
  local: { loading: false, error: "", plugins: [] },
  store: { loading: false, available: true, stale: false, error: "", fetchedAt: "", plugins: [] },
};
const detailState = {
  local: { name: "", loading: false, error: "", data: null },
  store: { name: "", loading: false, error: "", data: null },
};

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
  if (["incompatible", "replacement-conflict", "layout-conflict"].includes(plugin.status)) return "danger";
  if (["update-available", "replacement-available", "pending"].includes(plugin.status)) return "warning";
  if (plugin.status === "installed") return "ok";
  return "muted";
}

function statusMarkup(plugin, tab) {
  if (tab === "store") {
    return `<span class="badge ${storeStatusClass(plugin)}">${esc(storeStatusLabel(plugin))}</span>`;
  }
  return `<span class="badge ${runtimeClass(plugin)}">${esc(runtimeLabel(plugin))}</span>`;
}

function pluginSearchText(plugin) {
  const authors = Array.isArray(plugin.authors) ? plugin.authors.map(author => author?.name || "") : [];
  const tags = Array.isArray(plugin.tags) ? plugin.tags : [];
  return [plugin.name, plugin.displayName, plugin.description, plugin.gameName, plugin.kind, ...authors, ...tags]
    .filter(Boolean)
    .join(" ")
    .toLocaleLowerCase();
}

function filteredPlugins(tab) {
  const query = searchQuery.trim().toLocaleLowerCase();
  const plugins = Array.isArray(listState[tab].plugins) ? listState[tab].plugins : [];
  return query ? plugins.filter(plugin => pluginSearchText(plugin).includes(query)) : plugins;
}

function pluginListItem(plugin, tab) {
  const selected = selectedByTab[tab] === plugin.name;
  const version = plugin.version ? `v${plugin.version}` : "版本未标注";
  const description = plugin.description || (tab === "store" ? "官方插件" : "本地扩展能力");
  return `<button class="plugin-list-item${selected ? " is-selected" : ""}" type="button" role="option" aria-selected="${selected ? "true" : "false"}" data-action="select-plugin" data-tab="${tab}" data-name="${esc(plugin.name)}" data-testid="${tab === "store" ? "plugin-store-row" : "plugin-local-row"}"><span class="plugin-list-item-main">${pluginNameMarkup(plugin)}<span class="muted plugin-list-description">${esc(description)}</span></span><span class="plugin-list-item-meta">${pluginKindBadge(plugin)}${statusMarkup(plugin, tab)}<span class="badge muted">${esc(version)}</span></span></button>`;
}

function pluginLoadingContent(title, message, testId) {
  return `<div class="plugin-loading-state" data-testid="${testId}" role="status" aria-live="polite" aria-busy="true"><div class="plugin-loading-progress" role="progressbar" aria-label="${esc(title)}" aria-valuetext="正在获取目录"><span></span></div><strong>${esc(title)}</strong><span class="muted">${esc(message)}</span></div>`;
}

function storeWarningMarkup() {
  const data = listState.store;
  return data.stale
    ? `<div class="callout callout-warning" data-testid="plugin-store-stale">仓库连接暂时不可用，当前显示缓存目录。${data.error ? ` ${esc(data.error)}` : ""}</div>`
    : "";
}

function pluginListPaneMarkup(tab) {
  const data = listState[tab];
  const testId = tab === "store" ? "plugin-store-list" : "plugin-local-list";
  if (data.loading) {
    return `<section class="plugin-list-pane" data-testid="${testId}">${pluginLoadingContent(tab === "store" ? "正在加载插件仓库" : "正在加载本地插件", tab === "store" ? "正在获取官方插件目录，请稍候…" : "正在读取本机插件状态，请稍候…", `${tab === "store" ? "plugin-store" : "plugin-local"}-loading`)}</section>`;
  }
  if (tab === "store" && data.available === false) {
    return `<section class="plugin-list-pane" data-testid="${testId}"><div class="plugin-store-unavailable-message"><strong>插件仓库暂不可用</strong><span>${esc(data.error || "请检查网络连接或代理设置。")}</span></div></section>`;
  }
  if (data.error && !data.plugins.length) {
    return `<section class="plugin-list-pane" data-testid="${testId}"><div class="empty"><strong>加载本地插件失败</strong><span>${esc(data.error)}</span></div></section>`;
  }
  const plugins = filteredPlugins(tab);
  const empty = searchQuery.trim()
    ? `<div class="empty"><strong>没有匹配的插件</strong><span>尝试更换名称、标签或游戏关键词。</span></div>`
    : `<div class="empty"><strong>${tab === "store" ? "暂无可用插件" : "暂无本地插件"}</strong><span>${tab === "store" ? "官方插件目录当前没有可展示的条目。" : "从插件仓库安装插件后，重启服务即可加载。"}</span></div>`;
  return `<section class="plugin-list-pane" data-testid="${testId}">${storeWarningMarkup()}<div class="plugin-list" role="listbox" aria-label="${tab === "store" ? "插件仓库列表" : "本地插件列表"}">${plugins.length ? plugins.map(plugin => pluginListItem(plugin, tab)).join("") : empty}</div></section>`;
}

function authorMarkup(authors) {
  if (!Array.isArray(authors) || !authors.length) return `<span class="muted">未提供</span>`;
  return authors.map(author => {
    const name = esc(author?.name || "未知作者");
    const url = String(author?.url || "").trim();
    if (!url) return `<span>${name}</span>`;
    try {
      const parsed = new URL(url);
      if (parsed.protocol !== "https:") return `<span>${name}</span>`;
      return `<a href="${esc(parsed.href)}" target="_blank" rel="noopener noreferrer">${name}</a>`;
    } catch {
      return `<span>${name}</span>`;
    }
  }).join("、");
}

function tagsMarkup(tags) {
  if (!Array.isArray(tags) || !tags.length) return `<span class="muted">未提供</span>`;
  return `<span class="plugin-detail-tags">${tags.map(tag => `<span class="badge muted">${esc(tag)}</span>`).join("")}</span>`;
}

function changelogMarkup(entries) {
  const changes = Array.isArray(entries) ? entries : [];
  if (!changes.length) return `<div class="empty compact-empty"><span>暂无更新记录</span></div>`;
  return `<div class="plugin-detail-changelog">${changes.map(entry => `<article class="plugin-changelog-entry"><div class="plugin-changelog-version"><strong>v${esc(entry.version)}</strong><span class="muted">${esc(entry.date)}</span></div><ul>${(Array.isArray(entry.items) ? entry.items : []).map(item => `<li>${esc(item)}</li>`).join("")}</ul></article>`).join("")}</div>`;
}

function localActionMarkup(detail) {
  if (!detail) return "";
  const enabled = detail.configuredEnabled === true;
  return `<button class="tertiary" type="button" data-action="toggle-plugin" data-name="${esc(detail.name)}" data-enabled="${enabled ? "false" : "true"}">${enabled ? "禁用插件" : "启用插件"}</button>`;
}

function storeActionMarkup(detail) {
  if (!detail) return "";
  const pending = detail.status === "pending";
  const incompatible = detail.compatible === false;
  const conflicted = ["replacement-conflict", "layout-conflict"].includes(detail.status);
  if (pending) return `<span class="muted">${esc(detail.pendingAction === "uninstall" ? "卸载" : "安装")} v${esc(detail.pendingVersion || detail.version)}，重启后生效</span>`;
  if (incompatible) return `<span class="muted">${esc(detail.compatibilityReason || "当前宿主版本不兼容")}</span>`;
  if (conflicted) return `<span class="muted">${detail.status === "layout-conflict" ? "物理目录存在冲突，请先处理本地插件目录。" : "目标插件与旧插件同时存在，请先处理本地插件冲突。"}</span>`;
  if (detail.status === "unlisted") {
    return `<button class="danger" type="button" data-action="store-uninstall" data-name="${esc(detail.name)}" data-testid="plugin-uninstall-${esc(detail.name)}">卸载插件</button>`;
  }
  let result = "";
  if (!detail.installed) {
    result += `<button class="primary" type="button" data-action="store-install" data-name="${esc(detail.name)}" data-testid="plugin-install-${esc(detail.name)}">安装插件</button>`;
  } else if (detail.updateAvailable) {
    result += `<button class="primary" type="button" data-action="store-update" data-name="${esc(detail.name)}" data-testid="plugin-update-${esc(detail.name)}">${detail.status === "replacement-available" ? "替换旧插件" : "更新插件"}</button>`;
  }
  if (detail.installed && (!detail.installedName || detail.installedName === detail.name)) {
    result += `<button class="tertiary" type="button" data-action="store-uninstall" data-name="${esc(detail.name)}" data-testid="plugin-uninstall-${esc(detail.name)}">卸载插件</button>`;
  }
  return result || `<span class="muted">当前已是最新版本</span>`;
}

function detailMetaMarkup(detail, tab) {
  const rows = [
    ["版本", detail.version ? `v${detail.version}` : "未标注"],
    ["更新时间", detail.updatedAt || "未提供"],
    ["适用项目", detail.gameName || "通用"],
    ["插件类型", pluginKindLabel(detail)],
  ];
  if (tab === "store" && detail.minHostVersion && detail.minHostVersion !== "0.0.0") {
    rows.push(["最低宿主版本", `v${detail.minHostVersion}`]);
  }
  return `<dl class="plugin-detail-meta">${rows.map(([label, value]) => `<div><dt>${esc(label)}</dt><dd>${esc(value)}</dd></div>`).join("")}<div><dt>作者</dt><dd>${authorMarkup(detail.authors)}</dd></div><div><dt>标签</dt><dd>${tagsMarkup(detail.tags)}</dd></div>${detail.homepage ? `<div><dt>项目主页</dt><dd><a href="${esc(detail.homepage)}" target="_blank" rel="noopener noreferrer">打开主页</a></dd></div>` : ""}</dl>`;
}

function readmeMarkup(detail) {
  if (detail.readmeAvailable && detail.readmeMarkdown) {
    return renderMarkdown(detail.readmeMarkdown);
  }
  if (detail.readmeError) {
    return `<div class="callout callout-warning">${esc(detail.readmeError)}</div>`;
  }
  return `<div class="empty compact-empty"><span>${detail.hasReadme ? "README 暂时没有可显示的内容。" : "暂无 README。"}</span></div>`;
}

function detailContentMarkup(detail, tab) {
  const status = tab === "store" ? statusMarkup(detail, tab) : statusMarkup({ ...detail, state: detail.runtimeState }, tab);
  const actions = tab === "store" ? storeActionMarkup(detail) : localActionMarkup(detail);
  const runtimeError = tab === "local" && detail.runtimeError ? `<div class="field-error-message">${esc(detail.runtimeError)}</div>` : "";
  return `<div class="plugin-detail-head"><div class="plugin-detail-title"><div>${pluginKindBadge(detail)}${status}</div><h3>${esc(detail.displayName || detail.name)}</h3><code>${esc(detail.name)}</code></div><div class="plugin-detail-actions">${actions}</div></div><p class="plugin-detail-description">${esc(detail.description || "暂无简介")}</p>${runtimeError}${detail.installed && detail.installedVersion && detail.installedVersion !== detail.version ? `<div class="callout callout-warning">当前安装版本：v${esc(detail.installedVersion)}${detail.installedName && detail.installedName !== detail.name ? `（${esc(detail.installedName)}）` : ""}</div>` : ""}${detailMetaMarkup(detail, tab)}<section class="plugin-detail-section"><h4>README</h4>${readmeMarkup(detail)}</section><section class="plugin-detail-section"><h4>更新记录</h4>${changelogMarkup(detail.changelog)}</section>`;
}

function detailPaneMarkup(tab) {
  const current = detailState[tab];
  if (current.loading) {
    return `<section class="plugin-detail-pane" data-testid="plugin-detail"><div class="plugin-detail-loading" role="status" aria-live="polite"><div class="plugin-loading-progress" role="progressbar" aria-label="正在加载插件详情"><span></span></div><strong>正在加载插件详情</strong><span class="muted">正在读取 README 与更新记录，请稍候…</span></div></section>`;
  }
  if (current.error) {
    return `<section class="plugin-detail-pane" data-testid="plugin-detail"><div class="empty"><strong>插件详情加载失败</strong><span>${esc(current.error)}</span></div></section>`;
  }
  if (!current.data) {
    return `<section class="plugin-detail-pane" data-testid="plugin-detail"><div class="empty"><strong>选择一个插件</strong><span>从左侧列表选择插件查看完整信息。</span></div></section>`;
  }
  return `<section class="plugin-detail-pane" data-testid="plugin-detail"><button class="plugin-detail-back ghost" type="button" data-action="plugin-detail-back" data-testid="plugin-detail-back">返回插件列表</button>${detailContentMarkup(current.data, tab)}</section>`;
}

function pluginTabs(tab = activeTab) {
  const storeClass = tab === "store" ? "primary" : "tertiary";
  const localClass = tab === "local" ? "primary" : "tertiary";
  return `<div class="plugin-tabs" role="tablist" aria-label="插件视图"><button type="button" class="${localClass}" role="tab" aria-selected="${String(tab === "local")}" data-action="switch-plugin-tab" data-tab="local" data-testid="plugin-local-tab">本地插件</button><button type="button" class="${storeClass}" role="tab" aria-selected="${String(tab === "store")}" data-action="switch-plugin-tab" data-tab="store" data-testid="plugin-store-tab">插件仓库</button></div>`;
}

function pluginBrowserMarkup(tab) {
  const mobileClass = detailVisibleMobile ? " detail-visible" : "";
  const detailColumnClass = tab === "store" ? " has-store-footer" : "";
  const storeFooter = tab === "store"
    ? `<div class="plugin-browser-footer"><span class="muted">${listState.store.fetchedAt ? `目录更新时间：${esc(listState.store.fetchedAt)}` : ""}</span><button class="tertiary" type="button" data-action="store-refresh" data-testid="plugin-store-refresh">刷新仓库</button></div>`
    : "";
  return `<div class="plugin-browser${mobileClass}" data-testid="plugin-browser"><div class="plugin-list-column"><label class="plugin-search"><span class="sr-only">搜索插件名称、标签或游戏</span><input type="search" value="${esc(searchQuery)}" placeholder="搜索插件名称、标签或游戏" aria-label="搜索插件名称、标签或游戏" data-action="filter-plugin-list" data-testid="plugin-search" autocomplete="off"></label><div class="plugin-list-pane-slot">${pluginListPaneMarkup(tab)}</div></div><div class="plugin-detail-column${detailColumnClass}">${detailPaneMarkup(tab)}${storeFooter}</div></div>`;
}

function pluginPageMarkup(tab) {
  return pageHeader(
    "插件",
    "插件",
    tab === "store" ? "浏览官方插件并管理安装版本。" : "查看已安装插件并管理运行状态。",
    pluginTabs(tab),
  ) + pluginBrowserMarkup(tab);
}

function renderDetailPane() {
  const pane = document.querySelector(".plugin-detail-pane");
  if (!pane) return;
  pane.outerHTML = detailPaneMarkup(activeTab);
  document.querySelector(".plugin-browser")?.classList.toggle("detail-visible", detailVisibleMobile);
}

function renderListPane() {
  const slot = document.querySelector(".plugin-list-pane-slot");
  if (slot) slot.innerHTML = pluginListPaneMarkup(activeTab);
}

function selectDefaultPlugin(tab) {
  const plugins = Array.isArray(listState[tab].plugins) ? listState[tab].plugins : [];
  if (!plugins.some(plugin => plugin.name === selectedByTab[tab])) {
    selectedByTab[tab] = plugins[0]?.name || "";
  }
  return selectedByTab[tab];
}

async function loadDetail(tab, token) {
  const name = selectedByTab[tab];
  if (!name) {
    detailState[tab] = { name: "", loading: false, error: "", data: null };
    if (isCurrent("plugins", token) && activeTab === tab) renderDetailPane();
    return;
  }
  const id = ++detailLoadId;
  detailState[tab] = { name, loading: true, error: "", data: null };
  if (isCurrent("plugins", token) && activeTab === tab) renderDetailPane();
  try {
    const path = tab === "store"
      ? `/api/plugins/store/${encodeURIComponent(name)}/detail`
      : `/api/plugins/${encodeURIComponent(name)}/detail`;
    const data = await api("GET", path);
    if (!isCurrent("plugins", token) || id !== detailLoadId || activeTab !== tab || selectedByTab[tab] !== name) return;
    detailState[tab] = { name, loading: false, error: "", data };
    renderDetailPane();
  } catch (error) {
    if (!isCurrent("plugins", token) || id !== detailLoadId || activeTab !== tab || selectedByTab[tab] !== name) return;
    detailState[tab] = { name, loading: false, error: error.message || "详情读取失败", data: null };
    renderDetailPane();
  }
}

export async function pagePlugins(token) {
  if (!isCurrent("plugins", token)) return;
  navActive("plugins"); setTopbarTitle("插件");
  const requestedTab = activeTab;
  const loadId = ++pluginLoadId;
  detailVisibleMobile = false;
  listState[requestedTab] = requestedTab === "store"
    ? { loading: true, available: true, stale: false, error: "", fetchedAt: "", plugins: [] }
    : { loading: true, error: "", plugins: [] };
  detailState[requestedTab] = { name: "", loading: false, error: "", data: null };
  render(pluginPageMarkup(requestedTab));
  try {
    const data = requestedTab === "store"
      ? await api("GET", "/api/plugins/store")
      : await api("GET", "/api/plugins");
    if (!isCurrent("plugins", token) || loadId !== pluginLoadId || activeTab !== requestedTab) return;
    if (requestedTab === "store") {
      listState.store = {
        loading: false,
        available: data.available !== false,
        stale: data.stale === true,
        error: data.error || "",
        fetchedAt: data.fetchedAt || "",
        plugins: Array.isArray(data.plugins) ? data.plugins : [],
      };
    } else {
      const plugins = Array.isArray(data) ? data : [];
      listState.local = { loading: false, error: "", plugins };
      state.plugins = plugins;
    }
    selectDefaultPlugin(requestedTab);
    const name = selectedByTab[requestedTab];
    detailState[requestedTab] = { name, loading: Boolean(name), error: "", data: null };
    render(pluginPageMarkup(requestedTab));
    await loadDetail(requestedTab, token);
  } catch (error) {
    if (!isCurrent("plugins", token) || loadId !== pluginLoadId || activeTab !== requestedTab) return;
    listState[requestedTab] = requestedTab === "store"
      ? { loading: false, available: false, stale: false, error: error.message || "加载失败", fetchedAt: "", plugins: [] }
      : { loading: false, error: error.message || "加载失败", plugins: [] };
    detailState[requestedTab] = { name: "", loading: false, error: "", data: null };
    render(pluginPageMarkup(requestedTab));
  }
}

export async function togglePlugin(name, enabled) {
  try {
    await api("POST", `/api/plugins/${encodeURIComponent(name)}/${enabled ? "enable" : "disable"}`);
    markRestartRequired();
    toast("已更新（重启生效）");
    await pagePlugins(state.routeToken);
  } catch (error) {
    toast(error.message, "error");
  }
}

async function runStoreAction(name, action) {
  try {
    await api("POST", `/api/plugins/store/${encodeURIComponent(name)}/${action}`);
    markRestartRequired();
    toast(action === "uninstall" ? "已登记卸载（重启生效）" : "已登记操作（重启生效）");
    await pagePlugins(state.routeToken);
  } catch (error) {
    toast(error.message, "error");
  }
}

export const actions = {
  "toggle-plugin": target => togglePlugin(target.dataset.name, target.dataset.enabled === "true"),
  "select-plugin": target => {
    const tab = target.dataset.tab === "store" ? "store" : "local";
    const name = target.dataset.name || "";
    if (!name || activeTab !== tab) return;
    if (selectedByTab[tab] === name) {
      detailVisibleMobile = true;
      document.querySelector(".plugin-browser")?.classList.add("detail-visible");
      return;
    }
    selectedByTab[tab] = name;
    detailVisibleMobile = true;
    renderListPane();
    renderDetailPane();
    loadDetail(tab, state.routeToken);
  },
  "plugin-detail-back": () => {
    detailVisibleMobile = false;
    document.querySelector(".plugin-browser")?.classList.remove("detail-visible");
  },
  "filter-plugin-list": target => {
    searchQuery = target.value || "";
    renderListPane();
  },
  "switch-plugin-tab": target => {
    activeTab = target.dataset.tab === "store" ? "store" : "local";
    searchQuery = "";
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
