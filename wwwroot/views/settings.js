import { api } from "../core/api.js";
import { $ } from "../core/dom.js";
import { esc } from "../core/format.js";
import { selectField, valueField } from "../core/forms.js";
import { closeModal, confirmModal, modalShell, showModal } from "../core/modal.js";
import { isCurrent, schedule, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, toast, withBusy } from "../core/ui.js";

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
  // v0.7.3+（用户需求）：重启服务按钮移至右上角（同调度队列页新建按钮位置），主色样式；轻量模式不可用。
  const restartBtn = `<button type="button" data-action="restart-service" data-testid="restart-service" ${settings.lightweightMode ? "disabled" : ""}>重启服务</button>`;
  render(`<div class="page-head"><div><div class="eyebrow">系统设置</div><h2>设置</h2><p class="page-kicker">控制服务启动方式、历史保留和本地 Web 服务端口。</p></div>${restartBtn}</div><section class="card"><div class="section-heading"><h3>服务行为</h3><span class="muted">部分改动需重启生效</span></div><div class="toggle-grid"><button class="mode-toggle" type="button" data-action="toggle-st-flag" data-flag="st-autostart" id="st-autostart" aria-pressed="${settings.autoStart ? "true" : "false"}">开机自启</button><button class="mode-toggle" type="button" data-action="toggle-st-flag" data-flag="st-lightweight" id="st-lightweight" aria-pressed="${settings.lightweightMode ? "true" : "false"}">轻量模式</button><button class="mode-toggle" type="button" data-action="toggle-st-flag" data-flag="st-browser" id="st-browser" aria-pressed="${settings.autoOpenBrowser ? "true" : "false"}">打开浏览器</button></div><p class="muted helper-copy">开机自启：注册到当前用户启动项；轻量模式：不启动网页服务，重启生效；打开浏览器：服务启动后自动打开。修改即时自动保存。</p><div class="form-grid three">${valueField("st-retention", "历史保留天数", settings.historyRetentionDays, "number", 'min="1" max="180"')}${valueField("st-port", "Web 端口（重启生效）", settings.webPort, "number", 'min="1024" max="65535"')}${selectField("st-loglevel", "日志级别", settings.logLevel || "info", [{ value: "debug", label: "Debug" }, { value: "info", label: "Info" }, { value: "warn", label: "Warn" }, { value: "error", label: "Error" }, { value: "fatal", label: "Fatal" }])}</div><p class="muted helper-copy">日志级别：DEBUG 全部记录（含 Web 请求）；INFO 常规；WARN 仅警告与错误；ERROR 仅错误与致命；FATAL 仅致命错误。即时生效。</p>${settings.lightweightMode ? '<p class="muted helper-copy">轻量模式未启动 Web 服务，不支持自动重启，请手动重启程序。</p>' : ""}</section><section class="card"><div class="section-heading"><h3>远程访问</h3><span class="muted">默认仅本地，需重启生效</span></div><div class="toggle-row"><button class="mode-toggle" type="button" data-action="toggle-st-flag" data-flag="st-remote" id="st-remote" aria-pressed="${settings.allowRemoteAccess ? "true" : "false"}">远程访问</button><span class="muted">绑定所有网卡，所有 API 需携带访问令牌；本地 127.0.0.1 请求豁免</span></div><div class="field-btn-row">${valueField("st-token", "访问令牌", "", "password", 'autocomplete="new-password" placeholder="留空=不修改"')}<button type="button" class="ghost" data-action="toggle-token-visibility" aria-pressed="false">显示</button><button type="button" class="ghost" data-action="copy-token">复制</button><button type="button" class="ghost" data-action="gen-token">生成令牌</button></div><div id="remote-lan-list" class="detail">${lanList}</div>${settings.allowRemoteAccess ? '<p class="muted helper-copy lan-helper">其他设备请访问上述「局域网访问地址」（不要用 localhost / 0.0.0.0，它们只指向本机）；首次访问会要求输入访问令牌。</p>' : ""}<p class="muted helper-copy">安全提示：开启远程访问后，任何持有令牌的人都能完整管理本机脚本与配置。请勿在公共网络环境开启，令牌与配置数据绑定当前电脑（DPAPI 加密，不可迁移）。开启时程序会自动添加防火墙入站允许规则。</p></section><div class="helper-copy muted">通知渠道（Webhook / SMTP）请在「插件」页的通知推送插件配置中设置。</div>`);
  bindAutoSave();
}

/** 自动保存串行链（v0.7.3+ 用户需求：修改一次即保存一次，成功静默、失败 toast）：连续触发（快速切换开关）
 *  串行执行，避免并发 PUT 乱序覆盖；重启/离开前可 await 等待链完成。 */
let saveChain = Promise.resolve();

export function awaitSaveSettled() {
  return saveChain;
}

function autoSave() {
  const save = saveChain.then(() => doSave());
  saveChain = save.catch(error => toast(error.message, "error"));
  return save;
}

/** 收集当前控件值并 PUT 保存；成功后更新内存状态（不重渲染页面），远程开关变化后局部刷新地址列表。 */
async function doSave() {
  const token = ($("#st-token")?.value || "").trim();
  const payload = {
    autoStart: $("#st-autostart")?.getAttribute("aria-pressed") === "true",
    lightweightMode: $("#st-lightweight")?.getAttribute("aria-pressed") === "true",
    autoOpenBrowser: $("#st-browser")?.getAttribute("aria-pressed") === "true",
    historyRetentionDays: +($("#st-retention")?.value || 3),
    webPort: +($("#st-port")?.value || 58731),
    logLevel: $("#st-loglevel")?.value || "info",
    allowRemoteAccess: $("#st-remote")?.getAttribute("aria-pressed") === "true",
  };
  if (token) {
    payload.secretKey = "accessToken";
    payload.secretValue = token;
  }
  const data = await api("PUT", "/api/settings", payload);
  state.settings = data.settings;
  await refreshLanList();
}

/** 局部刷新局域网地址列表（远程访问开关切换后地址随之变化；失败静默保持旧内容）。 */
async function refreshLanList() {
  const box = $("#remote-lan-list");
  if (!box) return;
  try {
    const data = await api("GET", "/api/settings");
    state.settings = data.settings;
    const lan = (data.status && data.status.remote && data.status.remote.lanAddresses) || [];
    const helper = box.nextElementSibling?.classList.contains("lan-helper") ? box.nextElementSibling : null;
    box.innerHTML = data.settings.allowRemoteAccess && lan.length
      ? lan.map(addr => `<div class="kv"><span class="k">局域网访问地址</span><span>http://${esc(addr)}:${data.settings.webPort}/</span></div>`).join("")
      : "";
    if (helper) helper.hidden = !data.settings.allowRemoteAccess;
  } catch { /* 静默 */ }
}

/** 设置页控件自动保存绑定（v0.7.3+）：输入/下拉失焦（change）即保存；切换按钮经 toggle-st-flag 触发。 */
function bindAutoSave() {
  ["st-loglevel", "st-retention", "st-port", "st-token"].forEach(id => {
    $("#" + id)?.addEventListener("change", autoSave);
  });
}

/** 重启服务：等待挂起的自动保存完成后弹确认卡片（v0.7.3+ 端口改动已即时保存，无需再校验）。 */
export async function restartService() {
  await awaitSaveSettled();
  confirmModal("重启服务", "重启将中断正在运行的任务，页面会短暂断开连接。确认重启服务？", "restart-confirm");
}

export async function restartConfirmed() {
  let newPort = 0;
  try {
    const res = await api("POST", "/api/settings/restart");
    newPort = (res && res.newPort) || 0;
  } catch (error) {
    toast(error.message, "error");
    return;
  }
  const currentPort = Number(location.port || (location.protocol === "http:" ? 80 : 443));
  const candidates = [];
  for (const port of [currentPort, newPort, newPort + 1]) {
    if (port > 0 && !candidates.includes(port)) candidates.push(port);
  }
  showModal(modalShell("服务重启中", '<p class="modal-copy">服务正在重启，页面将短暂断开连接并自动恢复...</p>'), false, true);
  pollRestart(candidates, Date.now() + 60000);
}

/** 每 1 秒探测候选端口（当前端口 / 保存端口 / 端口漂移 +1 补偿），服务恢复后刷新页面或跳转到新端口；60 秒超时提示手动刷新。 */
function pollRestart(candidates, deadline) {
  schedule(async () => {
    const headers = {};
    // v0.7.4（KN-45）：存储不可用（隐私模式）时按无令牌处理，避免 getItem 抛异常中断重启轮询。
    let token = null;
    try {
      token = localStorage.getItem("nexus-token");
    } catch {
      token = null;
    }
    if (token) headers["Authorization"] = "Bearer " + token;
    for (const port of candidates) {
      try {
        const probe = new URL(location.href);
        probe.port = String(port);
        probe.pathname = "/api/status";
        probe.search = "";
        probe.hash = "";
        const res = await fetch(probe, { cache: "no-store", headers });
        if (res.ok) {
          if (port === Number(location.port || (location.protocol === "http:" ? 80 : 443))) {
            location.reload();
          } else {
            const target = new URL(location.href);
            target.port = String(port);
            target.pathname = "/";
            target.search = "";
            target.hash = "#/settings";
            location.href = target.href;
          }
          return;
        }
      } catch { /* 服务未就绪，继续轮询 */ }
    }
    if (Date.now() < deadline) {
      pollRestart(candidates, deadline);
    } else {
      closeModal();
      toast("服务重启超时，请手动刷新页面", "error");
    }
  }, 1000, "settings", state.routeToken);
}

export const actions = {
  "restart-service": () => restartService(),
  "restart-confirm": target => withBusy(target, () => restartConfirmed()),
  "toggle-st-flag": target => {
    const btn = $("#" + target.dataset.flag);
    if (!btn) return;
    btn.setAttribute("aria-pressed", btn.getAttribute("aria-pressed") === "true" ? "false" : "true");
    autoSave();
  },
  "toggle-token-visibility": target => {
    const input = $("#st-token");
    if (!input) return;
    const visible = input.type === "password";
    input.type = visible ? "text" : "password";
    target.setAttribute("aria-pressed", String(visible));
    target.textContent = visible ? "隐藏" : "显示";
  },
  "copy-token": async target => {
    const input = $("#st-token");
    const value = input?.value?.trim();
    if (!value) { toast("当前没有可复制的令牌", "error"); return; }
    try {
      await navigator.clipboard.writeText(value);
      toast("访问令牌已复制");
    } catch (error) {
      toast("复制访问令牌失败，请手动复制", "error");
    }
  },
  "gen-token": () => {
    const bytes = new Uint8Array(24);
    crypto.getRandomValues(bytes);
    const hex = Array.from(bytes, b => b.toString(16).padStart(2, "0")).join("");
    const input = $("#st-token");
    if (input) {
      input.value = hex;
      input.type = "password";
    }
    toast("已生成随机令牌，正在保存…");
    void autoSave().then(() => toast("访问令牌已保存"), () => {});
  },
};
