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
  const remote = data.status && data.status.remote;
  const lanList = (remote && remote.lanAddresses && remote.lanAddresses.length)
    ? remote.lanAddresses.map(addr => `<div class="kv"><span class="k">局域网访问地址</span><span>http://${esc(addr)}:${settings.webPort}/</span></div>`).join("")
    : "";
  render(`<div class="page-head"><div><div class="eyebrow">SYSTEM PREFERENCES</div><h2>设置</h2><p class="page-kicker">控制服务启动方式、历史保留和本地 Web 服务端口。</p></div></div><section class="card"><div class="section-heading"><h3>服务行为</h3><span class="muted">部分改动需重启生效</span></div><div class="toggle-grid"><button class="mode-toggle" type="button" data-action="toggle-st-flag" data-flag="st-autostart" id="st-autostart" aria-pressed="${settings.autoStart ? "true" : "false"}">开机自启</button><button class="mode-toggle" type="button" data-action="toggle-st-flag" data-flag="st-lightweight" id="st-lightweight" aria-pressed="${settings.lightweightMode ? "true" : "false"}">轻量模式</button><button class="mode-toggle" type="button" data-action="toggle-st-flag" data-flag="st-browser" id="st-browser" aria-pressed="${settings.autoOpenBrowser ? "true" : "false"}">打开浏览器</button></div><p class="muted helper-copy">开机自启：注册到当前用户启动项；轻量模式：不启动网页服务，重启生效；打开浏览器：服务启动后自动打开。</p><div class="form-grid three">${valueField("st-retention", "历史保留天数", settings.historyRetentionDays, "number", 'min="1" max="180"')}${valueField("st-port", "Web 端口（重启生效）", settings.webPort, "number", 'min="1024" max="65535"')}${selectField("st-loglevel", "日志级别", settings.logLevel || "info", [{ value: "debug", label: "Debug" }, { value: "info", label: "Info" }, { value: "warn", label: "Warn" }, { value: "error", label: "Error" }, { value: "fatal", label: "Fatal" }])}</div><p class="muted helper-copy">日志级别：DEBUG 全部记录（含 Web 请求）；INFO 常规；WARN 仅警告与错误；ERROR 仅错误与致命；FATAL 仅致命错误。即时生效。</p><div class="modal-footer-inline"><button type="button" data-action="save-settings">保存设置</button></div></section><section class="card"><div class="section-heading"><h3>远程访问</h3><span class="muted">默认仅本地，需重启生效</span></div><div class="toggle-row"><button class="mode-toggle" type="button" data-action="toggle-st-flag" data-flag="st-remote" id="st-remote" aria-pressed="${settings.allowRemoteAccess ? "true" : "false"}">远程访问</button><span class="muted">绑定所有网卡，所有 API 需携带访问令牌；本地 127.0.0.1 请求豁免</span></div><div class="field-btn-row">${valueField("st-token", "访问令牌", "", "password", 'placeholder="留空=不修改"')}<button type="button" class="ghost" data-action="gen-token">生成令牌</button></div>${lanList ? `<div class="detail">${lanList}</div><p class="muted helper-copy">其他设备请访问上述「局域网访问地址」（不要用 localhost / 0.0.0.0，它们只指向本机）；首次访问会要求输入访问令牌。</p>` : ""}<p class="muted helper-copy">安全提示：开启远程访问后，任何持有令牌的人都能完整管理本机脚本与配置。请勿在公共网络环境开启，令牌与配置数据绑定当前电脑（DPAPI 加密，不可迁移）。开启时程序会自动添加防火墙入站允许规则。</p><div class="modal-footer-inline"><button type="button" data-action="save-settings">保存设置</button></div></section><div class="helper-copy muted">通知渠道（Webhook / SMTP）请在「插件」页的通知推送插件配置中设置。</div>`);
}

export async function saveSettings() {
  const token = ($("#st-token")?.value || "").trim();
  const payload = { autoStart: $("#st-autostart")?.getAttribute("aria-pressed") === "true", lightweightMode: $("#st-lightweight")?.getAttribute("aria-pressed") === "true", autoOpenBrowser: $("#st-browser")?.getAttribute("aria-pressed") === "true", historyRetentionDays: +($("#st-retention")?.value || 3), webPort: +($("#st-port")?.value || 58731), logLevel: $("#st-loglevel")?.value || "info", allowRemoteAccess: $("#st-remote")?.getAttribute("aria-pressed") === "true" };
  if (token) {
    payload.secretKey = "accessToken";
    payload.secretValue = token;
  }
  try { await api("PUT", "/api/settings", payload); toast("设置已保存"); await pageSettings(state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "save-settings": () => saveSettings(),
  "toggle-st-flag": target => { const btn = $("#" + target.dataset.flag); if (btn) btn.setAttribute("aria-pressed", btn.getAttribute("aria-pressed") === "true" ? "false" : "true"); },
  "gen-token": () => {
    const bytes = new Uint8Array(24);
    crypto.getRandomValues(bytes);
    const hex = Array.from(bytes, b => b.toString(16).padStart(2, "0")).join("");
    const input = $("#st-token");
    if (input) {
      input.value = hex;
      input.type = "text";
    }
    toast("已生成随机令牌，保存后生效");
  },
};
