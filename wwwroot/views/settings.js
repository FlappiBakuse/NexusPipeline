import { api } from "../core/api.js";
import { $ } from "../core/dom.js";
import { esc } from "../core/format.js";
import { selectField, valueField } from "../core/forms.js";
import { isCurrent, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, toast } from "../core/ui.js";

export async function pageSettings(token) {
  if (!isCurrent("settings", token)) return;
  navActive("settings"); setTopbarTitle("设置");
  let data;
  try { data = await api("GET", "/api/settings"); }
  catch (error) { render(`<div class="empty"><strong>加载设置失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("settings", token)) return;
  state.settings = data.settings;
  const settings = data.settings;
  render(`<div class="page-head"><div><div class="eyebrow">SYSTEM PREFERENCES</div><h2>设置</h2><p class="page-kicker">控制服务启动方式、历史保留和本地 Web 服务端口。</p></div></div><section class="card"><div class="section-heading"><h3>服务行为</h3><span class="muted">部分改动需重启生效</span></div><div class="check-grid"><label class="check"><input id="st-autostart" type="checkbox" ${settings.autoStart ? "checked" : ""}><span>开机自启动（注册到当前用户启动项）</span></label><label class="check"><input id="st-lightweight" type="checkbox" ${settings.lightweightMode ? "checked" : ""}><span>轻量运行模式（不启动网页服务，重启生效）</span></label><label class="check"><input id="st-browser" type="checkbox" ${settings.autoOpenBrowser ? "checked" : ""}><span>服务启动后自动打开浏览器</span></label></div><div class="form-grid three">${valueField("st-retention", "历史保留天数", settings.historyRetentionDays, "number", 'min="1"')}${valueField("st-port", "Web 端口（重启生效）", settings.webPort, "number", 'min="1024" max="65535"')}${selectField("st-loglevel", "日志级别", settings.logLevel || "info", ["debug", "info", "warn", "error", "fatal"])}</div><p class="muted helper-copy">日志级别：DEBUG 全部记录（含 Web 请求）；INFO 常规；WARN 仅警告与错误；ERROR 仅错误与致命；FATAL 仅致命错误。即时生效。</p><div class="modal-footer-inline"><button type="button" data-action="save-settings">保存设置</button></div></section><div class="helper-copy muted">通知渠道（Webhook / SMTP）请在「插件」页的通知推送插件配置中设置。</div>`);
}

export async function saveSettings() {
  const payload = { autoStart: $("#st-autostart")?.checked || false, lightweightMode: $("#st-lightweight")?.checked || false, autoOpenBrowser: $("#st-browser")?.checked || false, historyRetentionDays: +($("#st-retention")?.value || 3), webPort: +($("#st-port")?.value || 58731), logLevel: $("#st-loglevel")?.value || "info" };
  try { await api("PUT", "/api/settings", payload); toast("设置已保存"); await pageSettings(state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "save-settings": () => saveSettings(),
};
