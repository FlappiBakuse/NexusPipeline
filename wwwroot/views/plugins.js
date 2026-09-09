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
import { applyTranslations, t } from "../core/i18n.js";

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
  return t(plugin.kind === "data-specialized" ? "ui.specialized_plugin" : "ui.general_plugin");
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
  if (runtimeState === "Active") return t(plugin.configuredEnabled ? "ui.running" : "ui.running_restart_required");
  if (runtimeState === "InitFailed") return t("ui.initialization_failed");
  if (runtimeState === "InitTimedOut") return t("ui.initialization_timed_out");
  if (runtimeState === "StartTimedOut") return t("ui.startup_timed_out");
  if (runtimeState === "StopTimedOut") return t("ui.stop_timed_out");
  if (runtimeState === "Incompatible") return t("ui.incompatible_api");
  if (runtimeState === "Loading") return t("ui.loading");
  return t(plugin.configuredEnabled ? "ui.restart_required" : "ui.disabled");
}

function runtimeClass(plugin) {
  const runtimeState = plugin.state || (plugin.runtimeEnabled ? "Active" : "Disabled");
  if (runtimeState === "Active") return "ok";
  if (["InitFailed", "InitTimedOut", "StartTimedOut", "StopTimedOut", "Incompatible"].includes(runtimeState)) return "danger";
  return "muted";
}

function storeStatusLabel(plugin) {
  return t({
    "not-installed": "ui.not_installed",
    installed: "ui.installed",
    "update-available": "ui.update_available",
    pending: "ui.restart_required",
    incompatible: "ui.incompatible_with_host",
    unlisted: "ui.not_listed_in_repository",
  }[plugin.status] || "ui.available");
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
  ["all", t("ui.all")],
  ["managed-code", t("ui.general_plugin")],
  ["data-specialized", t("ui.specialized_plugin")],
];

const pluginSortOptions = [
  ["name", t("ui.name_initial_or_pinyin")],
  ["createdAt", t("ui.created_at")],
  ["updatedAt", t("ui.updated_at")],
];

function pluginFilterOptionMarkup(testId, action, key, label, selected, dataName) {
  return `<button class="plugin-filter-option${selected ? " is-selected" : ""}" type="button" role="radio" aria-checked="${selected ? "true" : "false"}" data-action="${action}" data-${dataName}="${esc(key)}" data-testid="${testId}">${esc(label)}${selected ? icon("check", "plugin-filter-option-check") : ""}</button>`;
}

function pluginFilterPopoverMarkup(tab) {
  const view = pluginViewState[tab];
  const open = pluginFilterOpen && activeTab === tab;
  return `<div id="plugin-filter-popover-${tab}" class="plugin-filter-popover" role="dialog" aria-label="${t("ui.plugin_filters_and_sorting")}"${open ? "" : " hidden"}><fieldset class="plugin-filter-section"><legend>${t("ui.plugin_type")}</legend><div class="plugin-filter-options" role="radiogroup" aria-label="${t("ui.plugin_type")}">${pluginKindOptions.map(([key, label]) => pluginFilterOptionMarkup(`plugin-filter-kind-${key}`, "set-plugin-kind", key, label, view.kind === key, "kind")).join("")}</div></fieldset><fieldset class="plugin-filter-section"><legend>${t("ui.sort")}</legend><div class="plugin-filter-options" role="radiogroup" aria-label="${t("ui.sort_field")}">${pluginSortOptions.map(([key, label]) => pluginFilterOptionMarkup(`plugin-filter-sort-${key}`, "set-plugin-sort", key, label, view.sortBy === key, "sort")).join("")}</div><div class="plugin-filter-direction" role="radiogroup" aria-label="${t("ui.sort_direction")}">${pluginFilterOptionMarkup("plugin-filter-direction-asc", "set-plugin-direction", "asc", t("ui.ascending"), view.direction === "asc", "direction")}${pluginFilterOptionMarkup("plugin-filter-direction-desc", "set-plugin-direction", "desc", t("ui.descending"), view.direction === "desc", "direction")}</div></fieldset>${isPluginViewStateActive(view) ? `<button class="plugin-filter-reset ghost" type="button" data-action="reset-plugin-filter" data-testid="plugin-filter-reset">${t("ui.reset")}</button>` : ""}</div>`;
}

function pluginSearchToolbarMarkup(tab) {
  const view = pluginViewState[tab];
  const active = isPluginViewStateActive(view);
  return `<div class="plugin-search-toolbar"><label class="plugin-search"><span class="sr-only">${t("ui.search_plugin_names_tags_or_games")}</span><input type="search" value="${esc(view.query)}" placeholder="${t("ui.search_plugin_names_tags_or_games")}" aria-label="${t("ui.search_plugin_names_tags_or_games")}" data-action="filter-plugin-list" data-testid="plugin-search" autocomplete="off"></label><div class="plugin-filter-wrap"><button class="plugin-filter-trigger${active ? " is-active" : ""}" type="button" data-action="toggle-plugin-filter" data-testid="plugin-filter" aria-haspopup="dialog" aria-expanded="${pluginFilterOpen && activeTab === tab ? "true" : "false"}" aria-controls="plugin-filter-popover-${tab}">${icon("filter")}<span>${t("ui.filter")}</span><span class="plugin-filter-status" data-plugin-filter-status${active ? "" : " hidden"}>${t("ui.set")}</span></button>${pluginFilterPopoverMarkup(tab)}</div></div>`;
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
  applyTranslations(document.querySelector(`#plugin-filter-popover-${activeTab}`));
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
  const version = plugin.version ? `v${plugin.version}` : t("ui.version_not_specified");
  const description = plugin.description || t(tab === "store" ? "ui.official_plugin" : "ui.local_extension");
  return `<button class="plugin-list-item${selected ? " is-selected" : ""}" type="button" role="option" aria-selected="${selected ? "true" : "false"}" data-action="select-plugin" data-tab="${tab}" data-name="${esc(plugin.name)}" data-testid="${tab === "store" ? "plugin-store-row" : "plugin-local-row"}"><span class="plugin-list-item-main">${pluginNameMarkup(plugin)}<span class="muted plugin-list-description">${esc(description)}</span></span><span class="plugin-list-item-meta">${pluginKindBadge(plugin)}${statusMarkup(plugin, tab)}<span class="badge muted">${esc(version)}</span></span></button>`;
}

function pluginLoadingContent(title, message, testId) {
  const titleText = t(title, {}, title);
  const messageText = t(message, {}, message);
  return `<div class="plugin-loading-state" data-testid="${testId}" role="status" aria-live="polite" aria-busy="true"><div class="plugin-loading-progress" role="progressbar" aria-label="${esc(titleText)}" aria-valuetext="${esc(titleText)}"><span></span></div><strong>${esc(titleText)}</strong><span class="muted">${esc(messageText)}</span></div>`;
}

function storeWarningMarkup() {
  const data = listState.store;
  return data.stale
    ? `<div class="callout callout-warning" data-testid="plugin-store-stale">${esc(t("plugin.store.stale", {}, "The repository is temporarily unavailable. Showing the cached catalog."))}</div>`
    : "";
}

function pluginListPaneMarkup(tab) {
  const data = listState[tab];
  const testId = tab === "store" ? "plugin-store-list" : "plugin-local-list";
  if (data.loading && !data.loaded) {
    return `<section class="plugin-list-pane" data-testid="${testId}">${pluginLoadingContent(tab === "store" ? "ui.loading_plugin_repository" : "ui.loading_local_plugins", tab === "store" ? "ui.fetching_the_official_plugin_catalog" : "ui.reading_local_plugin_status", `${tab === "store" ? "plugin-store" : "plugin-local"}-loading`)}</section>`;
  }
  if (tab === "store" && data.available === false) {
    return `<section class="plugin-list-pane" data-testid="${testId}"><div class="plugin-store-unavailable-message"><strong>${esc(t("ui.plugin_repository_unavailable"))}</strong><span>${esc(data.error || t("ui.check_your_network_connection_or_proxy_settings"))}</span></div></section>`;
  }
  if (data.error && !data.plugins.length) {
    return `<section class="plugin-list-pane" data-testid="${testId}"><div class="empty"><strong>${esc(t("ui.failed_to_load_local_plugins"))}</strong><span>${esc(data.error)}</span></div></section>`;
  }
  const plugins = filteredPlugins(tab);
  const view = pluginViewState[tab];
  const hasFilter = view.query.trim() || isPluginViewStateActive(view);
  const empty = hasFilter
    ? `<div class="empty"><strong>${esc(t("ui.no_matching_plugins"))}</strong><span>${esc(t("ui.adjust_the_search_or_filter_and_try_again"))}</span></div>`
    : `<div class="empty"><strong>${esc(t(tab === "store" ? "ui.no_plugins_available" : "ui.no_local_plugins"))}</strong><span>${esc(t(tab === "store" ? "ui.the_official_plugin_catalog_has_no_entries_to_show" : "ui.install_a_plugin_from_the_repository_then_restart_the_service_to_load_it"))}</span></div>`;
  return `<section class="plugin-list-pane" data-testid="${testId}">${storeWarningMarkup()}<div class="plugin-list" role="listbox" aria-label="${esc(t(tab === "store" ? "ui.plugin_repository_list" : "ui.local_plugin_list"))}">${plugins.length ? plugins.map(plugin => pluginListItem(plugin, tab)).join("") : empty}</div></section>`;
}

function authorMarkup(authors) {
  if (!Array.isArray(authors) || !authors.length) return `<span class="muted">${t("ui.not_provided")}</span>`;
  return authors.map(author => {
    const name = esc(author?.name || t("ui.unknown_author"));
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
  if (!Array.isArray(tags) || !tags.length) return `<span class="muted">${t("ui.not_provided")}</span>`;
  return `<span class="plugin-detail-tags">${tags.map(tag => `<span class="badge muted">${esc(tag)}</span>`).join("")}</span>`;
}

function changelogMarkup(entries) {
  const changes = Array.isArray(entries) ? entries : [];
  if (!changes.length) return `<div class="empty compact-empty"><span>${t("ui.no_changelog_entries")}</span></div>`;
  return `<div class="plugin-detail-changelog">${changes.map(entry => `<article class="plugin-changelog-entry"><div class="plugin-changelog-version"><strong>v${esc(entry.version)}</strong><span class="muted">${esc(entry.date)}</span></div><ul>${(Array.isArray(entry.items) ? entry.items : []).map(item => `<li>${esc(item)}</li>`).join("")}</ul></article>`).join("")}</div>`;
}

function localActionMarkup(detail) {
  if (!detail) return "";
  const enabled = detail.configuredEnabled === true;
  return `<button class="tertiary" type="button" data-action="toggle-plugin" data-name="${esc(detail.name)}" data-enabled="${enabled ? "false" : "true"}">${t(enabled ? "ui.disable_plugin" : "ui.enable_plugin")}</button>`;
}

function storeActionMarkup(detail) {
  if (!detail) return "";
  const pending = detail.status === "pending";
  const incompatible = detail.compatible === false;
  if (pending) return `<span class="muted">${esc(t(detail.pendingAction === "uninstall" ? "ui.uninstall" : "ui.install"))} v${esc(detail.pendingVersion || detail.version)}，${t("ui.effective_after_restart")}</span>`;
  if (incompatible) return `<span class="muted">${esc(t("plugin.store.incompatible", {}, "The current host version is incompatible"))}</span>`;
  if (detail.status === "unlisted") {
    return `<button class="danger" type="button" data-action="store-uninstall" data-name="${esc(detail.name)}" data-testid="plugin-uninstall-${esc(detail.name)}">${t("ui.uninstall_plugin")}</button>`;
  }
  let result = "";
  if (!detail.installed) {
    result += `<button class="primary" type="button" data-action="store-install" data-name="${esc(detail.name)}" data-testid="plugin-install-${esc(detail.name)}">${t("ui.install_plugin")}</button>`;
  } else if (detail.updateAvailable) {
    result += `<button class="primary" type="button" data-action="store-update" data-name="${esc(detail.name)}" data-testid="plugin-update-${esc(detail.name)}">${t("ui.update_plugin")}</button>`;
  }
  if (detail.installed && (!detail.installedName || detail.installedName === detail.name)) {
    result += `<button class="tertiary" type="button" data-action="store-uninstall" data-name="${esc(detail.name)}" data-testid="plugin-uninstall-${esc(detail.name)}">${t("ui.uninstall_plugin")}</button>`;
  }
  return result || `<span class="muted">${t("ui.already_up_to_date")}</span>`;
}

function detailMetaMarkup(detail, tab) {
  const rows = [
    [t("ui.version"), detail.version ? `v${detail.version}` : t("ui.version_not_specified")],
    [t("ui.created_at"), detail.createdAt || t("ui.not_provided")],
    [t("ui.updated_at"), detail.updatedAt || t("ui.not_provided")],
    [t("ui.supported_project"), detail.gameName || t("ui.general")],
    [t("ui.plugin_type"), pluginKindLabel(detail)],
  ];
  if (tab === "store" && detail.minHostVersion && detail.minHostVersion !== "0.0.0") {
    rows.push([t("ui.minimum_host_version"), `v${detail.minHostVersion}`]);
  }
  const homepageClass = detail.homepage ? " has-homepage" : "";
  return `<dl class="plugin-detail-meta${homepageClass}">${rows.map(([label, value]) => `<div><dt>${esc(label)}</dt><dd>${esc(value)}</dd></div>`).join("")}<div><dt>${t("ui.author")}</dt><dd>${authorMarkup(detail.authors)}</dd></div><div><dt>${t("ui.tags")}</dt><dd>${tagsMarkup(detail.tags)}</dd></div>${detail.homepage ? `<div class="plugin-detail-homepage-row"><dt>${t("ui.project_homepage")}</dt><dd><a href="${esc(detail.homepage)}" target="_blank" rel="noopener noreferrer">${t("ui.open_homepage")}</a></dd></div>` : ""}</dl>`;
}

function readmeMarkup(detail) {
  if (detail.readmeAvailable && detail.readmeMarkdown) {
    return renderMarkdown(detail.readmeMarkdown);
  }
  if (detail.readmeErrorCode) {
    return `<div class="callout callout-warning">${esc(t("plugin.store.readme_error"))}</div>`;
  }
  return `<div class="empty compact-empty"><span>${detail.hasReadme ? t("ui.readme_has_no_displayable_content") : t("ui.no_readme")}</span></div>`;
}

function detailContentMarkup(detail, tab) {
  const status = tab === "store" ? statusMarkup(detail, tab) : statusMarkup({ ...detail, state: detail.runtimeState }, tab);
  const actions = tab === "store" ? storeActionMarkup(detail) : localActionMarkup(detail);
  const displayName = esc(detail.displayName || detail.name);
  const runtimeError = tab === "local" && detail.runtimeErrorCode ? `<div class="field-error-message">${esc(t("plugin.store.runtime_error"))}</div>` : "";
  return `<div class="plugin-detail-head"><div class="plugin-detail-title"><div>${pluginKindBadge(detail)}${status}</div><h3 class="plugin-detail-name-scroll" tabindex="0" title="${displayName}"><span class="plugin-detail-name-scroll-inner">${displayName}</span></h3></div><div class="plugin-detail-actions">${actions}</div></div><p class="plugin-detail-description">${esc(detail.description || t("ui.no_description"))}</p>${runtimeError}${detail.installed && detail.installedVersion && detail.installedVersion !== detail.version ? `<div class="callout callout-warning">${t("ui.installed_version_value", { version: esc(detail.installedVersion) })}</div>` : ""}${detailMetaMarkup(detail, tab)}<section class="plugin-detail-section"><h4>README</h4>${readmeMarkup(detail)}</section><section class="plugin-detail-section"><h4>${t("ui.changelog")}</h4>${changelogMarkup(detail.changelog)}</section>`;
}

function detailPaneMarkup(tab) {
  const current = detailState[tab];
  if (current.loading && !current.data) {
    return `<section class="plugin-detail-pane" data-testid="plugin-detail"><div class="plugin-detail-loading" role="status" aria-live="polite"><div class="plugin-loading-progress" role="progressbar" aria-label="${t("ui.loading_plugin_details")}"><span></span></div><strong>${t("ui.loading_plugin_details")}</strong><span class="muted">${t("ui.reading_the_readme_and_changelog")}</span></div></section>`;
  }
  if (current.error) {
    return `<section class="plugin-detail-pane" data-testid="plugin-detail"><div class="empty"><strong>${t("ui.failed_to_load_plugin_details")}</strong><span>${esc(current.error)}</span></div></section>`;
  }
  if (!current.data) {
    return `<section class="plugin-detail-pane" data-testid="plugin-detail"><div class="empty"><strong>${t("ui.select_a_plugin")}</strong><span>${t("ui.select_a_plugin_from_the_list_to_view_its_details")}</span></div></section>`;
  }
  return `<section class="plugin-detail-pane" data-testid="plugin-detail">${detailContentMarkup(current.data, tab)}</section>`;
}

function detailBackMarkup() {
  return `<button class="plugin-detail-back ghost" type="button" data-action="plugin-detail-back" data-testid="plugin-detail-back">${t("ui.back_to_plugin_list")}</button>`;
}

function pluginTabs(tab = activeTab) {
  const storeClass = tab === "store" ? "primary" : "tertiary";
  const localClass = tab === "local" ? "primary" : "tertiary";
  return `<div class="plugin-tabs plugin-page-tabs" role="tablist" aria-label="${esc(t("ui.plugin_view"))}"><button type="button" class="${localClass}" role="tab" aria-selected="${String(tab === "local")}" data-action="switch-plugin-tab" data-tab="local" data-testid="plugin-local-tab">${t("ui.local_plugins")}</button><button type="button" class="${storeClass}" role="tab" aria-selected="${String(tab === "store")}" data-action="switch-plugin-tab" data-tab="store" data-testid="plugin-store-tab">${t("ui.plugin_repository")}</button></div>`;
}

function pluginBrowserMarkup(tab) {
  const mobileClass = detailVisibleMobile ? " detail-visible" : "";
  const detailColumnClass = tab === "store" ? " has-store-footer" : "";
  const storeFooter = tab === "store"
    ? `<div class="plugin-browser-footer"><span class="muted">${listState.store.fetchedAt ? t("ui.catalog_updated_value", { time: esc(listState.store.fetchedAt) }) : ""}</span><span class="plugin-browser-footer-actions"><button class="primary" type="button" data-action="store-update-all" data-testid="plugin-store-update-all">${esc(t("plugin.store.update_all", {}, "Update all plugins"))}</button><button class="tertiary" type="button" data-action="store-refresh" data-testid="plugin-store-refresh">${t("ui.refresh_repository")}</button></span></div>`
    : "";
  return `<div class="plugin-browser${mobileClass}" data-testid="plugin-browser"><div class="plugin-list-column">${pluginSearchToolbarMarkup(tab)}<div class="plugin-list-pane-slot">${pluginListPaneMarkup(tab)}</div></div><div class="plugin-detail-column${detailColumnClass}">${detailPaneMarkup(tab)}${storeFooter}</div></div>`;
}

function pluginPageMarkup(tab) {
  return pageHeader(
    t("ui.plugin"),
    t("ui.plugin"),
    tab === "store" ? t("ui.browse_official_plugins_and_manage_installed_versions") : t("ui.view_installed_plugins_and_manage_runtime_status"),
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
  applyTranslations(parent);
  initAutoScroll(parent);
}

function renderListPane() {
  const slot = document.querySelector(".plugin-list-pane-slot");
  if (slot) {
    slot.innerHTML = pluginListPaneMarkup(activeTab);
    applyTranslations(slot);
  }
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
      error: cached ? "" : (error.message || t("ui.failed_to_read_details")),
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
  navActive("plugins"); setTopbarTitle(t("ui.plugin"));
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
        error: "",
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
    const message = error.message || t("ui.load_failed");
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
    toast(t("ui.updated_restart_required"));
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
    toast(t(action === "uninstall" ? "ui.uninstall_queued_restart_required" : "ui.action_queued_restart_required"));
    await pagePlugins(state.routeToken);
  } catch (error) {
    toast(error.message, "error");
  }
}

async function updateAllStorePlugins() {
  try {
    const result = await api("POST", "/api/plugins/store/update-all");
    const updated = Array.isArray(result?.updated) ? result.updated.length : 0;
    const failed = Array.isArray(result?.failed) ? result.failed.length : 0;
    if (updated > 0) {
      markPluginCacheDirty("local", "store");
      markRestartRequired();
    }
    const failureDetails = (result?.failed || []).slice(0, 5).map(item => {
      const detail = t(`api.error.${item.code || "internal_error"}`, item.args || {}, item.code || t("ui.update_failed"));
      return `${item.name || t("ui.unknown_plugin")}: ${detail}`;
    });
    const summary = updated + failed === 0
      ? t("plugin.store.update_all_none")
      : t("plugin.store.update_all_done", { updated, failed });
    toast(failureDetails.length ? `${summary}\n${failureDetails.join("\n")}` : summary, failed > 0 ? "error" : "info");
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
      toast(t("ui.plugin_repository_refreshed"));
      await pagePlugins(state.routeToken);
    } catch (error) {
      toast(error.message, "error");
    }
  }),
  "store-update-all": target => withBusy(target, updateAllStorePlugins),
  "store-install": target => withBusy(target, () => runStoreAction(target.dataset.name, "install")),
  "store-update": target => withBusy(target, () => runStoreAction(target.dataset.name, "update")),
  "store-uninstall": target => withBusy(target, () => runStoreAction(target.dataset.name, "uninstall")),
};
