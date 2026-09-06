import { api } from "../core/api.js";
import { esc } from "../core/format.js";
import { pageHeader } from "../core/forms.js";
import { icon } from "../core/icons.js";
import { renderMarkdown } from "../core/markdown.js";
import {
  createPluginViewState,
  defaultPluginViewState,
  filterAndSortPlugins,
  isPluginViewStateActive,
} from "../core/plugin-list.js";
import { isCurrent, state } from "../core/state.js";
import { initAutoScroll, navActive, render, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { markRestartRequired } from "./settings.js";

let activeTab = "local";
let pluginLoadId = 0;
let detailLoadId = 0;
let detailVisibleMobile = false;
let pluginFilterOpen = false;
let pluginFilterDismissalBound = false;
const listCacheTtl = 5 * 60 * 1000;

const selectedByTab = { local: "", store: "" };
const pluginViewState = {
  local: defaultPluginViewState(),
  store: defaultPluginViewState(),
};
const listState = {
  local: { loading: false, loaded: false, dirty: false, lastValidatedAt: 0, signature: "", error: "", plugins: [] },
  store: { loading: false, loaded: false, dirty: false, lastValidatedAt: 0, signature: "", available: true, stale: false, error: "", fetchedAt: "", plugins: [] },
};
const detailState = {
  local: { name: "", loading: false, error: "", data: null },
  store: { name: "", loading: false, error: "", data: null },
};
const detailCache = { local: new Map(), store: new Map() };

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
    pending: "待重启",
    incompatible: "宿主不兼容",
    unlisted: "未列入仓库",
  }[plugin.status] || "可用";
}

function storeStatusClass(plugin) {
  if (plugin.status === "incompatible") return "danger";
  if (["update-available", "pending"].includes(plugin.status)) return "warning";
  if (plugin.status === "installed") return "ok";
  return "muted";
}

function statusMarkup(plugin, tab) {
  if (tab === "store") {
    return `<span class="badge ${storeStatusClass(plugin)}">${esc(storeStatusLabel(plugin))}</span>`;
  }
  return `<span class="badge ${runtimeClass(plugin)}">${esc(runtimeLabel(plugin))}</span>`;
}

function filteredPlugins(tab) {
  const plugins = Array.isArray(listState[tab].plugins) ? listState[tab].plugins : [];
  return filterAndSortPlugins(plugins, pluginViewState[tab]);
}

const pluginKindOptions = [
  ["all", "全部"],
  ["managed-code", "通用插件"],
  ["data-specialized", "专项插件"],
];

const pluginSortOptions = [
  ["name", "名称（首字母 / 拼音）"],
  ["createdAt", "创建时间"],
  ["updatedAt", "更新时间"],
];

function pluginFilterOptionMarkup(testId, action, key, label, selected, dataName) {
  return `<button class="plugin-filter-option${selected ? " is-selected" : ""}" type="button" role="radio" aria-checked="${selected ? "true" : "false"}" data-action="${action}" data-${dataName}="${esc(key)}" data-testid="${testId}">${esc(label)}${selected ? icon("check", "plugin-filter-option-check") : ""}</button>`;
}

function pluginFilterPopoverMarkup(tab) {
  const view = pluginViewState[tab];
  const open = pluginFilterOpen && activeTab === tab;
  return `<div id="plugin-filter-popover-${tab}" class="plugin-filter-popover" role="dialog" aria-label="插件筛选与排序"${open ? "" : " hidden"}><fieldset class="plugin-filter-section"><legend>插件类型</legend><div class="plugin-filter-options" role="radiogroup" aria-label="插件类型">${pluginKindOptions.map(([key, label]) => pluginFilterOptionMarkup(`plugin-filter-kind-${key}`, "set-plugin-kind", key, label, view.kind === key, "kind")).join("")}</div></fieldset><fieldset class="plugin-filter-section"><legend>排序</legend><div class="plugin-filter-options" role="radiogroup" aria-label="排序字段">${pluginSortOptions.map(([key, label]) => pluginFilterOptionMarkup(`plugin-filter-sort-${key}`, "set-plugin-sort", key, label, view.sortBy === key, "sort")).join("")}</div><div class="plugin-filter-direction" role="radiogroup" aria-label="排序方向">${pluginFilterOptionMarkup("plugin-filter-direction-asc", "set-plugin-direction", "asc", "正序", view.direction === "asc", "direction")}${pluginFilterOptionMarkup("plugin-filter-direction-desc", "set-plugin-direction", "desc", "逆序", view.direction === "desc", "direction")}</div></fieldset>${isPluginViewStateActive(view) ? `<button class="plugin-filter-reset ghost" type="button" data-action="reset-plugin-filter" data-testid="plugin-filter-reset">恢复默认</button>` : ""}</div>`;
}

function pluginSearchToolbarMarkup(tab) {
  const view = pluginViewState[tab];
  const active = isPluginViewStateActive(view);
  return `<div class="plugin-search-toolbar"><label class="plugin-search"><span class="sr-only">搜索插件名称、标签或游戏</span><input type="search" value="${esc(view.query)}" placeholder="搜索插件名称、标签或游戏" aria-label="搜索插件名称、标签或游戏" data-action="filter-plugin-list" data-testid="plugin-search" autocomplete="off"></label><div class="plugin-filter-wrap"><button class="plugin-filter-trigger${active ? " is-active" : ""}" type="button" data-action="toggle-plugin-filter" data-testid="plugin-filter" aria-haspopup="dialog" aria-expanded="${pluginFilterOpen && activeTab === tab ? "true" : "false"}" aria-controls="plugin-filter-popover-${tab}">${icon("filter")}<span>筛选</span><span class="plugin-filter-status" data-plugin-filter-status${active ? "" : " hidden"}>已设置</span></button>${pluginFilterPopoverMarkup(tab)}</div></div>`;
}

function syncPluginFilterTrigger() {
  const trigger = document.querySelector('[data-testid="plugin-filter"]');
  if (!trigger) return;
  const active = isPluginViewStateActive(pluginViewState[activeTab]);
  trigger.classList.toggle("is-active", active);
  trigger.setAttribute("aria-expanded", String(pluginFilterOpen));
  trigger.querySelector("[data-plugin-filter-status]")?.toggleAttribute("hidden", !active);
}

function renderPluginFilterPopover() {
  const popover = document.querySelector(`#plugin-filter-popover-${activeTab}`);
  if (popover) popover.outerHTML = pluginFilterPopoverMarkup(activeTab);
  syncPluginFilterTrigger();
}

function closePluginFilter({ restoreFocus = false } = {}) {
  if (!pluginFilterOpen) return;
  pluginFilterOpen = false;
  const popover = document.querySelector(`#plugin-filter-popover-${activeTab}`);
  if (popover) popover.hidden = true;
  const trigger = document.querySelector('[data-testid="plugin-filter"]');
  if (trigger) {
    trigger.setAttribute("aria-expanded", "false");
    if (restoreFocus) trigger.focus({ preventScroll: true });
  }
}

function togglePluginFilter() {
  if (pluginFilterOpen) {
    closePluginFilter();
    return;
  }
  pluginFilterOpen = true;
  const popover = document.querySelector(`#plugin-filter-popover-${activeTab}`);
  const trigger = document.querySelector('[data-testid="plugin-filter"]');
  if (popover) popover.hidden = false;
  if (trigger) trigger.setAttribute("aria-expanded", "true");
  popover?.querySelector('[role="radio"][aria-checked="true"]')?.focus({ preventScroll: true });
}

function bindPluginFilterDismissal() {
  if (pluginFilterDismissalBound) return;
  pluginFilterDismissalBound = true;
  document.addEventListener("pointerdown", event => {
    if (!pluginFilterOpen || event.target?.closest?.(".plugin-filter-wrap")) return;
    closePluginFilter();
  });
  document.addEventListener("keydown", event => {
    if (event.key !== "Escape" || !pluginFilterOpen || document.querySelector(".modal[data-locked]")) return;
    event.preventDefault();
    closePluginFilter({ restoreFocus: true });
  }, true);
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
  if (data.loading && !data.loaded) {
    return `<section class="plugin-list-pane" data-testid="${testId}">${pluginLoadingContent(tab === "store" ? "正在加载插件仓库" : "正在加载本地插件", tab === "store" ? "正在获取官方插件目录，请稍候…" : "正在读取本机插件状态，请稍候…", `${tab === "store" ? "plugin-store" : "plugin-local"}-loading`)}</section>`;
  }
  if (tab === "store" && data.available === false) {
    return `<section class="plugin-list-pane" data-testid="${testId}"><div class="plugin-store-unavailable-message"><strong>插件仓库暂不可用</strong><span>${esc(data.error || "请检查网络连接或代理设置。")}</span></div></section>`;
  }
  if (data.error && !data.plugins.length) {
    return `<section class="plugin-list-pane" data-testid="${testId}"><div class="empty"><strong>加载本地插件失败</strong><span>${esc(data.error)}</span></div></section>`;
  }
  const plugins = filteredPlugins(tab);
  const view = pluginViewState[tab];
  const hasFilter = view.query.trim() || isPluginViewStateActive(view);
  const empty = hasFilter
    ? `<div class="empty"><strong>没有匹配的插件</strong><span>调整搜索或筛选条件后重试。</span></div>`
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
  if (pending) return `<span class="muted">${esc(detail.pendingAction === "uninstall" ? "卸载" : "安装")} v${esc(detail.pendingVersion || detail.version)}，重启后生效</span>`;
  if (incompatible) return `<span class="muted">${esc(detail.compatibilityReason || "当前宿主版本不兼容")}</span>`;
  if (detail.status === "unlisted") {
    return `<button class="danger" type="button" data-action="store-uninstall" data-name="${esc(detail.name)}" data-testid="plugin-uninstall-${esc(detail.name)}">卸载插件</button>`;
  }
  let result = "";
  if (!detail.installed) {
    result += `<button class="primary" type="button" data-action="store-install" data-name="${esc(detail.name)}" data-testid="plugin-install-${esc(detail.name)}">安装插件</button>`;
  } else if (detail.updateAvailable) {
    result += `<button class="primary" type="button" data-action="store-update" data-name="${esc(detail.name)}" data-testid="plugin-update-${esc(detail.name)}">更新插件</button>`;
  }
  if (detail.installed && (!detail.installedName || detail.installedName === detail.name)) {
    result += `<button class="tertiary" type="button" data-action="store-uninstall" data-name="${esc(detail.name)}" data-testid="plugin-uninstall-${esc(detail.name)}">卸载插件</button>`;
  }
  return result || `<span class="muted">当前已是最新版本</span>`;
}

function detailMetaMarkup(detail, tab) {
  const rows = [
    ["版本", detail.version ? `v${detail.version}` : "未标注"],
    ["创建时间", detail.createdAt || "未提供"],
    ["更新时间", detail.updatedAt || "未提供"],
    ["适用项目", detail.gameName || "通用"],
    ["插件类型", pluginKindLabel(detail)],
  ];
  if (tab === "store" && detail.minHostVersion && detail.minHostVersion !== "0.0.0") {
    rows.push(["最低宿主版本", `v${detail.minHostVersion}`]);
  }
  const homepageClass = detail.homepage ? " has-homepage" : "";
  return `<dl class="plugin-detail-meta${homepageClass}">${rows.map(([label, value]) => `<div><dt>${esc(label)}</dt><dd>${esc(value)}</dd></div>`).join("")}<div><dt>作者</dt><dd>${authorMarkup(detail.authors)}</dd></div><div><dt>标签</dt><dd>${tagsMarkup(detail.tags)}</dd></div>${detail.homepage ? `<div class="plugin-detail-homepage-row"><dt>项目主页</dt><dd><a href="${esc(detail.homepage)}" target="_blank" rel="noopener noreferrer">打开主页</a></dd></div>` : ""}</dl>`;
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
  const displayName = esc(detail.displayName || detail.name);
  const runtimeError = tab === "local" && detail.runtimeError ? `<div class="field-error-message">${esc(detail.runtimeError)}</div>` : "";
  return `<div class="plugin-detail-head"><div class="plugin-detail-title"><div>${pluginKindBadge(detail)}${status}</div><h3 class="plugin-detail-name-scroll" tabindex="0" title="${displayName}"><span class="plugin-detail-name-scroll-inner">${displayName}</span></h3></div><div class="plugin-detail-actions">${actions}</div></div><p class="plugin-detail-description">${esc(detail.description || "暂无简介")}</p>${runtimeError}${detail.installed && detail.installedVersion && detail.installedVersion !== detail.version ? `<div class="callout callout-warning">当前安装版本：v${esc(detail.installedVersion)}</div>` : ""}${detailMetaMarkup(detail, tab)}<section class="plugin-detail-section"><h4>README</h4>${readmeMarkup(detail)}</section><section class="plugin-detail-section"><h4>更新记录</h4>${changelogMarkup(detail.changelog)}</section>`;
}

function detailPaneMarkup(tab) {
  const current = detailState[tab];
  if (current.loading && !current.data) {
    return `<section class="plugin-detail-pane" data-testid="plugin-detail"><div class="plugin-detail-loading" role="status" aria-live="polite"><div class="plugin-loading-progress" role="progressbar" aria-label="正在加载插件详情"><span></span></div><strong>正在加载插件详情</strong><span class="muted">正在读取 README 与更新记录，请稍候…</span></div></section>`;
  }
  if (current.error) {
    return `<section class="plugin-detail-pane" data-testid="plugin-detail"><div class="empty"><strong>插件详情加载失败</strong><span>${esc(current.error)}</span></div></section>`;
  }
  if (!current.data) {
    return `<section class="plugin-detail-pane" data-testid="plugin-detail"><div class="empty"><strong>选择一个插件</strong><span>从左侧列表选择插件查看完整信息。</span></div></section>`;
  }
  return `<section class="plugin-detail-pane" data-testid="plugin-detail">${detailContentMarkup(current.data, tab)}</section>`;
}

function detailBackMarkup() {
  return `<button class="plugin-detail-back ghost" type="button" data-action="plugin-detail-back" data-testid="plugin-detail-back">返回插件列表</button>`;
}

function pluginTabs(tab = activeTab) {
  const storeClass = tab === "store" ? "primary" : "tertiary";
  const localClass = tab === "local" ? "primary" : "tertiary";
  return `<div class="plugin-tabs plugin-page-tabs" role="tablist" aria-label="插件视图"><button type="button" class="${localClass}" role="tab" aria-selected="${String(tab === "local")}" data-action="switch-plugin-tab" data-tab="local" data-testid="plugin-local-tab">本地插件</button><button type="button" class="${storeClass}" role="tab" aria-selected="${String(tab === "store")}" data-action="switch-plugin-tab" data-tab="store" data-testid="plugin-store-tab">插件仓库</button></div>`;
}

function pluginBrowserMarkup(tab) {
  const mobileClass = detailVisibleMobile ? " detail-visible" : "";
  const detailColumnClass = tab === "store" ? " has-store-footer" : "";
  const storeFooter = tab === "store"
    ? `<div class="plugin-browser-footer"><span class="muted">${listState.store.fetchedAt ? `目录更新时间：${esc(listState.store.fetchedAt)}` : ""}</span><button class="tertiary" type="button" data-action="store-refresh" data-testid="plugin-store-refresh">刷新仓库</button></div>`
    : "";
  return `<div class="plugin-browser${mobileClass}" data-testid="plugin-browser"><div class="plugin-list-column">${pluginSearchToolbarMarkup(tab)}<div class="plugin-list-pane-slot">${pluginListPaneMarkup(tab)}</div></div><div class="plugin-detail-column${detailColumnClass}">${detailPaneMarkup(tab)}${storeFooter}</div></div>`;
}

function pluginPageMarkup(tab) {
  return pageHeader(
    "插件",
    "插件",
    tab === "store" ? "浏览官方插件并管理安装版本。" : "查看已安装插件并管理运行状态。",
    pluginTabs(tab),
    "plugin-page-head",
  ) + pluginBrowserMarkup(tab);
}

function renderDetailPane() {
  const pane = document.querySelector(".plugin-detail-pane");
  if (!pane) return;
  const parent = pane.parentElement;
  if (!parent) return;
  document.querySelector(".plugin-browser")?.classList.toggle("detail-visible", detailVisibleMobile);
  const back = parent.querySelector(".plugin-detail-back");
  if (detailVisibleMobile && !back) parent.insertAdjacentHTML("afterbegin", detailBackMarkup());
  if (!detailVisibleMobile) back?.remove();
  pane.outerHTML = detailPaneMarkup(activeTab);
  initAutoScroll(parent);
}

function renderListPane() {
  const slot = document.querySelector(".plugin-list-pane-slot");
  if (slot) slot.innerHTML = pluginListPaneMarkup(activeTab);
}

function selectDefaultPlugin(tab) {
  const plugins = filteredPlugins(tab);
  if (!plugins.some(plugin => plugin.name === selectedByTab[tab])) {
    selectedByTab[tab] = plugins[0]?.name || "";
  }
  return selectedByTab[tab];
}

async function loadDetail(tab, token, force = false) {
  const name = selectedByTab[tab];
  if (!name) {
    detailState[tab] = { name: "", loading: false, error: "", data: null };
    if (isCurrent("plugins", token) && activeTab === tab) renderDetailPane();
    return;
  }
  const cached = detailCache[tab].get(name) || null;
  if (cached && !force) {
    detailState[tab] = { name, loading: false, error: "", data: cached };
    if (isCurrent("plugins", token) && activeTab === tab) renderDetailPane();
    return;
  }
  const id = ++detailLoadId;
  detailState[tab] = { name, loading: true, error: "", data: cached };
  if (isCurrent("plugins", token) && activeTab === tab) renderDetailPane();
  try {
    const path = tab === "store"
      ? `/api/plugins/store/${encodeURIComponent(name)}/detail`
      : `/api/plugins/${encodeURIComponent(name)}/detail`;
    const data = await api("GET", path);
    if (!isCurrent("plugins", token) || id !== detailLoadId || activeTab !== tab || selectedByTab[tab] !== name) return;
    detailCache[tab].set(name, data);
    detailState[tab] = { name, loading: false, error: "", data };
    renderDetailPane();
  } catch (error) {
    if (!isCurrent("plugins", token) || id !== detailLoadId || activeTab !== tab || selectedByTab[tab] !== name) return;
    detailState[tab] = {
      name,
      loading: false,
      error: cached ? "" : (error.message || "详情读取失败"),
      data: cached,
    };
    renderDetailPane();
  }
}

function listSignature(plugins) {
  try {
    return JSON.stringify(Array.isArray(plugins) ? plugins : []);
  } catch {
    return "";
  }
}

function listCacheIsFresh(tab) {
  const data = listState[tab];
  return data.loaded
    && !data.dirty
    && data.lastValidatedAt > 0
    && Date.now() - data.lastValidatedAt < listCacheTtl;
}

function prepareSelectedDetail(tab) {
  selectDefaultPlugin(tab);
  const name = selectedByTab[tab];
  const cached = name ? detailCache[tab].get(name) || null : null;
  detailState[tab] = { name, loading: Boolean(name && !cached), error: "", data: cached };
}

function reconcileSelectedPlugin(tab) {
  const visible = filteredPlugins(tab);
  if (visible.some(plugin => plugin.name === selectedByTab[tab])) return false;
  selectedByTab[tab] = visible[0]?.name || "";
  const name = selectedByTab[tab];
  const cached = name ? detailCache[tab].get(name) || null : null;
  detailState[tab] = { name, loading: Boolean(name && !cached), error: "", data: cached };
  return true;
}

function updatePluginViewState(tab, patch, focusSelector = "") {
  pluginViewState[tab] = createPluginViewState({ ...pluginViewState[tab], ...patch });
  const changed = reconcileSelectedPlugin(tab);
  renderListPane();
  renderDetailPane();
  renderPluginFilterPopover();
  if (focusSelector) document.querySelector(focusSelector)?.focus({ preventScroll: true });
  if (changed && selectedByTab[tab]) void loadDetail(tab, state.routeToken);
}

function markPluginCacheDirty(...tabs) {
  for (const tab of tabs) {
    if (!listState[tab]) continue;
    listState[tab] = { ...listState[tab], dirty: true };
  }
}

export async function pagePlugins(token) {
  if (!isCurrent("plugins", token)) return;
  navActive("plugins"); setTopbarTitle("插件");
  pluginFilterOpen = false;
  const requestedTab = activeTab;
  const loadId = ++pluginLoadId;
  detailVisibleMobile = false;
  const cached = listState[requestedTab];
  if (!cached.loaded) {
    listState[requestedTab] = requestedTab === "store"
      ? { ...cached, loading: true, available: true, stale: false, error: "", fetchedAt: "", plugins: [] }
      : { ...cached, loading: true, error: "", plugins: [] };
    detailState[requestedTab] = { name: "", loading: false, error: "", data: null };
  } else {
    prepareSelectedDetail(requestedTab);
  }
  render(pluginPageMarkup(requestedTab));
  bindPluginFilterDismissal();
  if (listCacheIsFresh(requestedTab)) {
    if (selectedByTab[requestedTab] && !detailState[requestedTab].data) {
      await loadDetail(requestedTab, token);
    }
    return;
  }
  listState[requestedTab] = { ...listState[requestedTab], loading: true };
  render(pluginPageMarkup(requestedTab));
  bindPluginFilterDismissal();
  try {
    const data = requestedTab === "store"
      ? await api("GET", "/api/plugins/store")
      : await api("GET", "/api/plugins");
    if (!isCurrent("plugins", token) || loadId !== pluginLoadId || activeTab !== requestedTab) return;
    if (requestedTab === "store") {
      const plugins = Array.isArray(data.plugins) ? data.plugins : [];
      const signature = listSignature(plugins);
      if (signature !== listState.store.signature) detailCache.store.clear();
      listState.store = {
        loading: false,
        loaded: true,
        dirty: false,
        lastValidatedAt: Date.now(),
        signature,
        available: data.available !== false,
        stale: data.stale === true,
        error: data.error || "",
        fetchedAt: data.fetchedAt || "",
        plugins,
      };
    } else {
      const plugins = Array.isArray(data) ? data : [];
      const signature = listSignature(plugins);
      if (signature !== listState.local.signature) detailCache.local.clear();
      listState.local = {
        loading: false,
        loaded: true,
        dirty: false,
        lastValidatedAt: Date.now(),
        signature,
        error: "",
        plugins,
      };
      state.plugins = plugins;
    }
    prepareSelectedDetail(requestedTab);
    render(pluginPageMarkup(requestedTab));
    bindPluginFilterDismissal();
    if (selectedByTab[requestedTab] && !detailState[requestedTab].data) {
      await loadDetail(requestedTab, token);
    }
  } catch (error) {
    if (!isCurrent("plugins", token) || loadId !== pluginLoadId || activeTab !== requestedTab) return;
    const message = error.message || "加载失败";
    if (listState[requestedTab].loaded) {
      listState[requestedTab] = {
        ...listState[requestedTab],
        loading: false,
        dirty: true,
        stale: requestedTab === "store" ? true : undefined,
        error: message,
      };
      prepareSelectedDetail(requestedTab);
    } else {
      listState[requestedTab] = requestedTab === "store"
        ? { ...listState[requestedTab], loading: false, available: false, stale: false, error: message, plugins: [] }
        : { ...listState[requestedTab], loading: false, error: message, plugins: [] };
      detailState[requestedTab] = { name: "", loading: false, error: "", data: null };
    }
    render(pluginPageMarkup(requestedTab));
    bindPluginFilterDismissal();
  }
}

export async function togglePlugin(name, enabled) {
  try {
    await api("POST", `/api/plugins/${encodeURIComponent(name)}/${enabled ? "enable" : "disable"}`);
    markPluginCacheDirty("local", "store");
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
    markPluginCacheDirty("local", "store");
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
      renderDetailPane();
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
    document.querySelector(".plugin-detail-back")?.remove();
  },
  "filter-plugin-list": target => {
    const tab = activeTab;
    pluginViewState[tab] = createPluginViewState({ ...pluginViewState[tab], query: target.value || "" });
    const changed = reconcileSelectedPlugin(tab);
    renderListPane();
    renderDetailPane();
    if (changed && selectedByTab[tab]) void loadDetail(tab, state.routeToken);
  },
  "toggle-plugin-filter": () => togglePluginFilter(),
  "set-plugin-kind": target => {
    const key = target.dataset.kind || "all";
    updatePluginViewState(activeTab, { kind: key }, `[data-testid="plugin-filter-kind-${key}"]`);
  },
  "set-plugin-sort": target => {
    const key = target.dataset.sort || "name";
    updatePluginViewState(activeTab, { sortBy: key }, `[data-testid="plugin-filter-sort-${key}"]`);
  },
  "set-plugin-direction": target => {
    const key = target.dataset.direction || "asc";
    updatePluginViewState(activeTab, { direction: key }, `[data-testid="plugin-filter-direction-${key}"]`);
  },
  "reset-plugin-filter": () => {
    updatePluginViewState(activeTab, defaultPluginViewState(), '[data-testid="plugin-filter-reset"]');
  },
  "switch-plugin-tab": target => {
    closePluginFilter();
    activeTab = target.dataset.tab === "store" ? "store" : "local";
    pagePlugins(state.routeToken);
  },
  "store-refresh": target => withBusy(target, async () => {
    try {
      await api("POST", "/api/plugins/store/refresh");
      markPluginCacheDirty("store");
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
