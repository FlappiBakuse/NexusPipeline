import { api } from "../core/api.js";
import { $ } from "../core/dom.js";
import { esc } from "../core/format.js";
import { selectField, valueField } from "../core/forms.js";
import { isCurrent, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, toast } from "../core/ui.js";

export async function pagePlugins(token) {
  if (!isCurrent("plugins", token)) return;
  navActive("plugins"); setTopbarTitle("插件");
  let status;
  try { status = await api("GET", "/api/status"); }
  catch (error) { render(`<div class="empty"><strong>加载插件失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("plugins", token)) return;
  const plugins = status.plugins || [];
  render(`<div class="page-head"><div><div class="eyebrow">EXTENSIONS</div><h2>插件</h2><p class="page-kicker">管理通知推送和本地扩展能力。</p></div></div><section class="card"><div class="table-scroll"><table class="data-table"><thead><tr><th>名称</th><th>版本</th><th>说明</th><th>状态</th><th>操作</th></tr></thead><tbody>${plugins.map(plugin => `<tr><td><strong>${esc(plugin.displayName)}</strong></td><td>${esc(plugin.version)}</td><td class="muted">${esc(plugin.description)}</td><td>${plugin.enabled ? '<span class="badge ok">已启用</span>' : '<span class="badge muted">已禁用</span>'}</td><td>${plugin.name === "notify" ? '<button class="sm" type="button" data-action="plugin-config" data-name="notify">配置</button>' : ""}<button class="sm" type="button" data-action="toggle-plugin" data-name="${esc(plugin.name)}" data-enabled="${!plugin.enabled}">${plugin.enabled ? "禁用" : "启用"}</button></td></tr>`).join("")}</tbody></table></div><p class="muted helper-copy">内置插件修改状态后需重启服务生效；plugins 目录下的外部 DLL 插件会自动加载。</p></section>`);
}

export async function togglePlugin(name, enabled) {
  try { await api("POST", "/api/plugins/" + name + "/" + (enabled ? "enable" : "disable")); toast("已更新（重启生效）"); await pagePlugins(state.routeToken); }
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
    <section class="card"><button class="panel-toggle" type="button" data-action="toggle-panel" data-panel="panel-wh" aria-expanded="true"><span class="panel-arrow" id="arrow-wh">▾</span><span class="panel-label">Webhook 通知</span><span class="badge ${settings.webhookEnabled ? "ok" : "muted"}">${settings.webhookEnabled ? "已启用" : "已禁用"}</span></button><div id="panel-wh" class="panel-body"><label class="check"><input id="st-wh-enabled" type="checkbox" ${settings.webhookEnabled ? "checked" : ""}><span>启用 Webhook 通知</span></label><div class="form-grid">${selectField("st-whtype", "Webhook 类型", settings.webhookType, ["feishu", "dingtalk", "wecom", "slack", "discord", "generic"])} ${valueField("st-whtimeout", "超时秒数", settings.webhookTimeout || 30, "number", 'min="1"')}</div><label class="field-label" for="st-whurl">Webhook 地址 ${settings.webhookUrl ? '<span class="badge ok">已设置</span>' : ""}</label><div class="secret-row"><input id="st-whurl" type="password" placeholder="${settings.webhookUrl ? "（已设置，留空不变）" : "https://..."}"><button class="sm" type="button" data-action="save-secret" data-secret="webhookUrl" data-input="st-whurl">保存地址</button></div><label class="field-label" for="st-whsec">Webhook 签名密钥 ${settings.webhookSecret ? '<span class="badge ok">已设置</span>' : ""}</label><div class="secret-row"><input id="st-whsec" type="password" placeholder="${settings.webhookSecret ? "（已设置，留空不变）" : ""}"><button class="sm" type="button" data-action="save-secret" data-secret="webhookSecret" data-input="st-whsec">保存密钥</button></div><label class="field-label" for="st-whtpl">generic 自定义模板（JSON，{text} 为消息占位符）</label><textarea id="st-whtpl">${esc(settings.webhookTemplate)}</textarea></div>
    <button class="panel-toggle" type="button" data-action="toggle-panel" data-panel="panel-smtp" aria-expanded="false"><span class="panel-arrow" id="arrow-smtp">▸</span><span class="panel-label">SMTP 邮件通知</span><span class="badge ${settings.smtpEnabled ? "ok" : "muted"}">${settings.smtpEnabled ? "已启用" : "已禁用"}</span></button><div id="panel-smtp" class="panel-body" hidden><label class="check"><input id="st-smtp-enabled" type="checkbox" ${settings.smtpEnabled ? "checked" : ""}><span>启用 SMTP 邮件通知</span></label><div class="form-grid three">${valueField("st-host", "SMTP 服务器", settings.smtpHost)}${valueField("st-port2", "端口", settings.smtpPort, "number")}${selectField("st-secure", "加密方式", settings.smtpSecure, ["auto", "ssl", "starttls", "none"])}</div><div class="form-grid">${valueField("st-user", "账号", settings.smtpUser)}<div><label class="field-label" for="st-pwd">授权码 ${settings.smtpPassword ? '<span class="badge ok">已设置</span>' : ""}</label><div class="secret-row"><input id="st-pwd" type="password" placeholder="${settings.smtpPassword ? "（已设置，留空不变）" : ""}"><button class="sm" type="button" data-action="save-secret" data-secret="smtpPassword" data-input="st-pwd">保存</button></div></div></div><div class="form-grid">${valueField("st-to", "收件人（逗号分隔）", settings.smtpTo)}${valueField("st-from", "发件人显示地址（留空=账号）", settings.smtpFrom)}</div><div class="form-grid">${valueField("st-subject", "主题前缀", settings.smtpSubjectPrefix)}${valueField("st-smtp-timeout", "超时秒数", settings.smtpTimeout || 30, "number", 'min="1"')}</div></div><div class="modal-footer-inline"><button type="button" data-action="save-notify-settings">保存设置</button><button class="ghost" type="button" data-action="test-notify">测试通知</button></div></section>`;
  render(`<div class="page-head"><div><a class="back-link" href="#/plugins">← 返回插件</a><div class="eyebrow">PLUGIN CONFIGURATION</div><h2>${esc(plugin?.displayName || name)} · 配置</h2><p class="page-kicker">Webhook 和 SMTP 的密钥会在服务端加密保存。</p></div><button type="button" data-action="save-notify-settings">保存设置</button></div>${body}`);
}

export function togglePanel(panelId, trigger) {
  const panel = $("#" + panelId); if (!panel) return;
  const hidden = panel.hasAttribute("hidden");
  panel.toggleAttribute("hidden", !hidden);
  if (trigger) { trigger.setAttribute("aria-expanded", String(hidden)); const arrow = trigger.querySelector(".panel-arrow"); if (arrow) arrow.textContent = hidden ? "▾" : "▸"; }
}

export async function saveNotifySettings() {
  const payload = { webhookEnabled: $("#st-wh-enabled")?.checked || false, smtpEnabled: $("#st-smtp-enabled")?.checked || false, webhookType: $("#st-whtype")?.value || "generic", webhookTimeout: +($("#st-whtimeout")?.value || 30), webhookTemplate: $("#st-whtpl")?.value.trim() || "", smtpHost: $("#st-host")?.value.trim() || "", smtpPort: +($("#st-port2")?.value || 465), smtpSecure: $("#st-secure")?.value || "auto", smtpUser: $("#st-user")?.value.trim() || "", smtpTo: $("#st-to")?.value.trim() || "", smtpFrom: $("#st-from")?.value.trim() || "", smtpSubjectPrefix: $("#st-subject")?.value.trim() || "", smtpTimeout: +($("#st-smtp-timeout")?.value || 30) };
  try { await api("PUT", "/api/settings", payload); toast("通知设置已保存"); await pagePluginConfig("notify", state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export async function saveSecret(key, inputId) {
  const value = $("#" + inputId)?.value.trim() || "";
  try { await api("PUT", "/api/settings", { secretKey: key, secretValue: value }); toast(value ? "已加密保存" : "已清除"); await pagePluginConfig("notify", state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export async function testNotify() {
  try { const result = await api("POST", "/api/settings/test"); toast(result.ok ? "测试通知发送成功" : "发送失败，详见日志", result.ok ? "info" : "error"); }
  catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "plugin-config": target => { location.hash = "#/plugins/" + target.dataset.name; },
  "toggle-plugin": target => togglePlugin(target.dataset.name, target.dataset.enabled === "true"),
  "toggle-panel": target => togglePanel(target.dataset.panel, target),
  "save-notify-settings": () => saveNotifySettings(),
  "save-secret": target => saveSecret(target.dataset.secret, target.dataset.input),
  "test-notify": () => testNotify(),
};
