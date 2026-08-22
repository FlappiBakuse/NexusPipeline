import { api } from "../core/api.js";
import { $ } from "../core/dom.js";
import { esc } from "../core/format.js";
import { pageHeader, selectField, switchControl, valueField } from "../core/forms.js";
import { icon } from "../core/icons.js";
import { isCurrent, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { markRestartRequired } from "./settings.js";

export async function pagePlugins(token) {
  if (!isCurrent("plugins", token)) return;
  navActive("plugins"); setTopbarTitle("插件");
  let status;
  try { status = await api("GET", "/api/status"); }
  catch (error) { render(`<div class="empty"><strong>加载插件失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("plugins", token)) return;
  const plugins = status.plugins || [];
  const groups = [
    { label: "内置插件", items: plugins.filter(plugin => plugin.isBuiltIn) },
    { label: "专用能力", items: plugins.filter(plugin => !plugin.isBuiltIn && plugin.kind === "specialized") },
    { label: "通用能力", items: plugins.filter(plugin => !plugin.isBuiltIn && plugin.kind !== "specialized") },
  ].filter(group => group.items.length);
  const groupMarkup = groups.map(group => `<section class="plugin-group"><div class="plugin-group-heading"><h3>${group.label}</h3><span>${group.items.length} 项</span></div>${group.items.map(plugin => `<article class="plugin-row"><div class="plugin-row-main"><strong class="plugin-name-scroll" tabindex="0" title="${esc(plugin.displayName)}"><span class="plugin-name-scroll-inner">${esc(plugin.displayName)}</span></strong><span class="muted">${esc(plugin.description || "本地扩展能力")} · ${esc(plugin.version)}</span></div><span class="badge ${plugin.enabled ? "ok" : "muted"}">${plugin.enabled ? "已启用" : "已禁用"}</span><div class="plugin-row-action row-actions">${plugin.name === "notify" ? '<button class="tertiary" type="button" data-action="plugin-config" data-name="notify">配置</button>' : ""}<button class="tertiary" type="button" data-action="toggle-plugin" data-name="${esc(plugin.name)}" data-enabled="${!plugin.enabled}">${plugin.enabled ? "禁用" : "启用"}</button></div></article>`).join("")}</section>`).join("");
  render(pageHeader("插件", "插件", "管理通知推送和本地扩展能力。") + `<section class="plugins-table plugin-groups" data-testid="plugins-list">${groupMarkup || '<div class="empty"><strong>暂无插件</strong><span>服务启动后会自动加载可用扩展。</span></div>'}</section><p class="muted helper-copy plugin-helper">插件状态变化会在服务重启后完整生效。</p>`);
}

export async function togglePlugin(name, enabled) {
  try { await api("POST", "/api/plugins/" + name + "/" + (enabled ? "enable" : "disable")); markRestartRequired(); toast("已更新（重启生效）"); await pagePlugins(state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export async function pagePluginConfig(name, token) {
  const page = "plugins/" + name;
  if (!isCurrent(page, token)) return;
  navActive("plugins"); setTopbarTitle("插件配置");
  let data, status;
  try { [data, status] = await Promise.all([api("GET", "/api/settings"), api("GET", "/api/status")]); }
  catch (error) { render(`<div class="empty"><strong>加载插件配置失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent(page, token)) return;
  state.settings = data.settings;
  const settings = data.settings; const stats = status.notifyStats || {}; const plugin = (status.plugins || []).find(item => item.name === name);
  const body = `<section class="card"><div class="section-heading"><h3>配置信息</h3><span class="badge ${plugin?.enabled ? "ok" : "muted"}">${plugin?.enabled ? "已启用" : "已禁用"}</span></div><div class="stats-inline"><div><strong>${stats.enabledScripts ?? 0}</strong><span> 个启用通知的脚本实例</span></div><div><strong>${stats.enabledQueues ?? 0}</strong><span> 个启用通知的调度队列</span></div></div></section>
    <section class="card"><button class="panel-toggle" type="button" data-action="toggle-panel" data-panel="panel-wh" aria-expanded="true" aria-controls="panel-wh"><span class="panel-arrow" id="arrow-wh">▾</span><span class="panel-label">Webhook 通知</span><span class="badge ${settings.webhookEnabled ? "ok" : "muted"}">${settings.webhookEnabled ? "已启用" : "已禁用"}</span></button><div id="panel-wh" class="panel-body">${switchControl("st-wh-enabled", "启用 Webhook", "发送运行状态到 Webhook 服务", settings.webhookEnabled, "toggle-pn-flag", 'data-flag="st-wh-enabled"')}<div class="form-grid">${selectField("st-whtype", "Webhook 类型", settings.webhookType, [{ value: "feishu", label: "Feishu" }, { value: "dingtalk", label: "Dingtalk" }, { value: "wecom", label: "WeCom" }, { value: "slack", label: "Slack" }, { value: "discord", label: "Discord" }, { value: "generic", label: "Generic" }], 'data-action="toggle-generic-template"')} ${valueField("st-whtimeout", "超时秒数", settings.webhookTimeout || 30, "number", 'min="1"')}</div><div class="form-grid"><div class="field"><label class="field-label" for="st-whurl">Webhook 地址 ${settings.webhookUrl ? '<span class="badge ok">已设置</span>' : ""}</label><input id="st-whurl" type="text" placeholder="${settings.webhookUrl ? "（已设置，留空不变）" : "https://…"}"><p id="st-whurl-error" class="field-error-message" role="alert" hidden></p></div><div class="field"><label class="field-label" for="st-whsec">Webhook 签名密钥 ${settings.webhookSecret ? '<span class="badge ok">已设置</span>' : ""}</label><input id="st-whsec" type="password" placeholder="${settings.webhookSecret ? "（已设置，留空不变）" : ""}"><p id="st-whsec-error" class="field-error-message" role="alert" hidden></p></div></div><div id="st-whtpl-box" ${settings.webhookType === "generic" ? "" : "hidden"}><label class="field-label" for="st-whtpl">generic 自定义模板（JSON，{text} 为消息占位符）</label><textarea id="st-whtpl">${esc(settings.webhookTemplate)}</textarea></div></div>
    <button class="panel-toggle" type="button" data-action="toggle-panel" data-panel="panel-smtp" aria-expanded="false" aria-controls="panel-smtp"><span class="panel-arrow" id="arrow-smtp">▸</span><span class="panel-label">SMTP 邮件通知</span><span class="badge ${settings.smtpEnabled ? "ok" : "muted"}">${settings.smtpEnabled ? "已启用" : "已禁用"}</span></button><div id="panel-smtp" class="panel-body" hidden>${switchControl("st-smtp-enabled", "启用 SMTP", "发送运行状态到邮箱", settings.smtpEnabled, "toggle-pn-flag", 'data-flag="st-smtp-enabled"')}<div class="form-grid three">${valueField("st-host", "SMTP 服务器", settings.smtpHost)}${valueField("st-port2", "端口", settings.smtpPort, "number")}${selectField("st-secure", "加密方式", settings.smtpSecure, ["auto", "ssl", "starttls", "none"])}</div><div class="form-grid">${valueField("st-user", "账号", settings.smtpUser)}<div class="field"><label class="field-label" for="st-pwd">授权码 ${settings.smtpPassword ? '<span class="badge ok">已设置</span>' : ""}</label><input id="st-pwd" type="password" placeholder="${settings.smtpPassword ? "（已设置，留空不变）" : ""}"><p id="st-pwd-error" class="field-error-message" role="alert" hidden></p></div></div><div class="form-grid">${valueField("st-to", "收件人（逗号分隔）", settings.smtpTo)}${valueField("st-from", "发件人显示地址（留空=账号）", settings.smtpFrom)}</div><div class="form-grid">${valueField("st-subject", "主题前缀", settings.smtpSubjectPrefix)}${valueField("st-smtp-timeout", "超时秒数", settings.smtpTimeout || 30, "number", 'min="1"')}</div></div><div class="modal-footer-inline plain"><button type="button" data-action="save-notify-settings">保存设置</button><button class="ghost" type="button" data-action="test-notify">测试通知</button></div></section>`;
  const renderedBody = body.replaceAll("▾", icon("chevronDown", "icon panel-arrow-icon")).replaceAll("▸", icon("chevronRight", "icon panel-arrow-icon"));
  render(pageHeader("插件配置", `${esc(plugin?.displayName || name)} · 配置`, "Webhook 和 SMTP 的密钥会在服务端加密保存。", `<a class="back-link" href="#/plugins">${icon("arrowLeft")} 返回插件</a>`) + renderedBody);
}

export function togglePanel(panelId, trigger) {
  const panel = $("#" + panelId); if (!panel) return;
  const hidden = panel.hasAttribute("hidden");
  panel.toggleAttribute("hidden", !hidden);
  if (trigger) { trigger.setAttribute("aria-expanded", String(hidden)); const arrow = trigger.querySelector(".panel-arrow"); if (arrow) arrow.innerHTML = icon(hidden ? "chevronDown" : "chevronRight", "icon panel-arrow-icon"); }
}

export function toggleGenericTemplate() {
  const box = $("#st-whtpl-box"); if (!box) return;
  box.toggleAttribute("hidden", ($("#st-whtype")?.value || "") !== "generic");
}

export async function saveNotifySettings() {
  const payload = { webhookEnabled: $("#st-wh-enabled")?.getAttribute("aria-pressed") === "true", smtpEnabled: $("#st-smtp-enabled")?.getAttribute("aria-pressed") === "true", webhookType: $("#st-whtype")?.value || "generic", webhookTimeout: +($("#st-whtimeout")?.value || 30), webhookTemplate: $("#st-whtpl")?.value.trim() || "", smtpHost: $("#st-host")?.value.trim() || "", smtpPort: +($("#st-port2")?.value || 465), smtpSecure: $("#st-secure")?.value || "auto", smtpUser: $("#st-user")?.value.trim() || "", smtpTo: $("#st-to")?.value.trim() || "", smtpFrom: $("#st-from")?.value.trim() || "", smtpSubjectPrefix: $("#st-subject")?.value.trim() || "", smtpTimeout: +($("#st-smtp-timeout")?.value || 30) };
  const secrets = [
    ["webhookUrl", $("#st-whurl")?.value.trim() || ""],
    ["webhookSecret", $("#st-whsec")?.value.trim() || ""],
    ["smtpPassword", $("#st-pwd")?.value.trim() || ""],
  ].filter(([, value]) => value.length > 0);
  try {
    await api("PUT", "/api/settings", payload);
    for (const [key, value] of secrets) {
      await api("PUT", "/api/settings", { secretKey: key, secretValue: value });
    }
    toast(secrets.length ? "通知设置与密钥已保存" : "通知设置已保存");
    await pagePluginConfig("notify", state.routeToken);
  } catch (error) { toast(error.message, "error"); }
}

export async function testNotify() {
  try { const result = await api("POST", "/api/settings/test"); toast(result.ok ? "测试通知发送成功" : "发送失败，详见日志", result.ok ? "info" : "error"); }
  catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "plugin-config": target => { location.hash = "#/plugins/" + target.dataset.name; },
  "toggle-plugin": target => togglePlugin(target.dataset.name, target.dataset.enabled === "true"),
  "toggle-panel": target => togglePanel(target.dataset.panel, target),
  "toggle-generic-template": () => toggleGenericTemplate(),
  "toggle-pn-flag": target => { const btn = $("#" + target.dataset.flag); if (btn) btn.setAttribute("aria-pressed", btn.getAttribute("aria-pressed") === "true" ? "false" : "true"); },
  "save-notify-settings": target => withBusy(target, () => saveNotifySettings()),
  "test-notify": target => withBusy(target, () => testNotify()),
};
