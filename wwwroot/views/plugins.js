import { api } from "../core/api.js";
import { esc } from "../core/format.js";
import { pageHeader } from "../core/forms.js";
import { isCurrent, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, toast } from "../core/ui.js";
import { markRestartRequired } from "./settings.js";

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

export async function pagePlugins(token) {
  if (!isCurrent("plugins", token)) return;
  navActive("plugins"); setTopbarTitle("插件");
  let status;
  try { status = await api("GET", "/api/status"); }
  catch (error) { render(`<div class="empty"><strong>加载插件失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("plugins", token)) return;
  const plugins = status.plugins || [];
  const groups = [
    { label: "数据化专项插件", items: plugins.filter(plugin => plugin.kind === "data-specialized") },
    { label: "代码插件", items: plugins.filter(plugin => plugin.kind === "managed-code") },
    { label: "其他插件", items: plugins.filter(plugin => !["data-specialized", "managed-code"].includes(plugin.kind)) },
  ].filter(group => group.items.length);
  const groupMarkup = groups.map(group => `<section class="plugin-group"><div class="plugin-group-heading"><h3>${group.label}</h3><span>${group.items.length} 项</span></div>${group.items.map(pluginRow).join("")}</section>`).join("");
  render(pageHeader("插件", "插件", "管理数据化专项插件和外部代码插件。") + `<section class="plugins-table plugin-groups" data-testid="plugins-list">${groupMarkup || '<div class="empty"><strong>暂无插件</strong><span>服务启动后会自动加载可用扩展。</span></div>'}</section><p class="muted helper-copy plugin-helper">插件状态变化会在服务重启后完整生效。通知渠道请在「设置」页配置。</p>`);
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

export const actions = {
  "toggle-plugin": target => togglePlugin(target.dataset.name, target.dataset.enabled === "true"),
};
