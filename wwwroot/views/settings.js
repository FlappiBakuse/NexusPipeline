import { api } from "../core/api.js";
import { $ } from "../core/dom.js";
import { esc } from "../core/format.js";
import { pageHeader, selectField, switchControl, valueField } from "../core/forms.js";
import { icon } from "../core/icons.js";
import { closeModal, confirmModal, modalShell, showModal } from "../core/modal.js";
import { isCurrent, schedule, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, toast, withBusy } from "../core/ui.js";

let restartRequired = false;

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
  render(pageHeader("系统设置", "设置", "控制服务启动方式、历史保留和本地 Web 服务端口。") + restartNoticeMarkup(settings) + `<section class="card section-surface"><div class="section-heading"><h3>服务行为</h3><span class="muted">开关即时保存，需要重启的改动会在这里提示</span></div><div class="settings-list">
    ${switchControl("st-autostart", "开机自启", "注册到当前用户启动项", settings.autoStart, "toggle-st-flag", 'data-flag="st-autostart"')}
    ${switchControl("st-lightweight", "轻量模式", "不启动网页服务，重启后生效", settings.lightweightMode, "toggle-st-flag", 'data-flag="st-lightweight" data-restart-required="true"')}
    ${switchControl("st-browser", "打开浏览器", "服务启动后自动打开控制台", settings.autoOpenBrowser, "toggle-st-flag", 'data-flag="st-browser"')}
  </div><div class="form-grid three">${valueField("st-retention", "历史保留天数", settings.historyRetentionDays, "number", 'min="1" max="180"')}${valueField("st-port", "Web 端口", settings.webPort, "number", 'min="1024" max="65535"')}${selectField("st-loglevel", "日志级别", settings.logLevel || "info", [{ value: "debug", label: "Debug" }, { value: "info", label: "Info" }, { value: "warn", label: "Warn" }, { value: "error", label: "Error" }, { value: "fatal", label: "Fatal" }])}</div><p class="muted helper-copy">日志级别即时生效；Web 端口改动需重启服务。</p>${settings.lightweightMode ? '<p class="muted helper-copy">轻量模式未启动 Web 服务，重启请手动操作。</p>' : ""}</section><section class="card section-surface"><div class="section-heading"><h3>远程访问</h3><span class="muted">绑定所有网卡，重启后生效</span></div>${switchControl("st-remote", "远程访问", "绑定所有网卡，API 需要访问令牌；本地 127.0.0.1 请求豁免", settings.allowRemoteAccess, "toggle-st-flag", 'data-flag="st-remote" data-restart-required="true"')}<div class="field-btn-row">${valueField("st-token", "访问令牌", "", "password", 'autocomplete="new-password" placeholder="留空=不修改"')}<button type="button" class="ghost" data-action="toggle-token-visibility" data-testid="toggle-token-visibility" aria-pressed="false">显示</button><button type="button" class="ghost" data-action="copy-token">复制</button><button type="button" class="ghost" data-action="gen-token" data-testid="gen-token">生成令牌</button></div><div id="remote-lan-list" class="detail">${lanList}</div>${settings.allowRemoteAccess ? '<p class="muted helper-copy lan-helper">其他设备请访问上述「局域网访问地址」（不要用 localhost / 0.0.0.0，它们只指向本机）；首次访问会要求输入访问令牌。</p>' : ""}<p class="callout callout-warning">安全提示：开启远程访问后，持有令牌的人都能管理本机脚本与配置。请勿在公共网络环境开启，令牌与配置数据绑定当前电脑（DPAPI 加密，不可迁移）。开启时程序会自动添加防火墙入站允许规则。</p></section>`);
  const view = document.querySelector("#view");
  view?.insertAdjacentHTML("beforeend", notificationSettingsMarkup(settings));
  view?.insertAdjacentHTML("beforeend", updateSectionMarkup(settings, null));
  bindAutoSave();
  renderUpdateStatus();
  loadUpdateStatus();
}

function restartNoticeMarkup(settings) {
  if (!restartRequired) return "";
  const disabled = settings.lightweightMode;
  return `<section id="restart-notice" class="dashboard-system-note" role="status" aria-live="polite"><p>有需要重启服务的设置已保存。</p>${disabled ? '<span class="muted">轻量模式请手动重启程序。</span>' : '<button class="primary" type="button" data-action="restart-service" data-testid="restart-service">重启服务</button>'}</section>`;
}

function notificationSettingsMarkup(settings) {
  const body = `<section class="card section-surface notification-settings"><div class="section-heading"><div><h3>通知渠道</h3><span class="muted">Webhook 和 SMTP 是宿主内置能力，供脚本、队列和代码插件使用。</span></div></div>
    <button class="panel-toggle" type="button" data-action="toggle-panel" data-panel="panel-wh" aria-expanded="true" aria-controls="panel-wh"><span class="panel-arrow" id="arrow-wh">▾</span><span class="panel-label">Webhook 通知</span><span class="badge ${settings.webhookEnabled ? "ok" : "muted"}">${settings.webhookEnabled ? "已启用" : "已禁用"}</span></button><div id="panel-wh" class="panel-body">${switchControl("st-wh-enabled", "启用 Webhook", "发送运行状态到 Webhook 服务", settings.webhookEnabled, "toggle-notify-flag", 'data-flag="st-wh-enabled"')}<div class="form-grid">${selectField("st-whtype", "Webhook 类型", settings.webhookType, [{ value: "feishu", label: "Feishu" }, { value: "dingtalk", label: "Dingtalk" }, { value: "wecom", label: "WeCom" }, { value: "slack", label: "Slack" }, { value: "discord", label: "Discord" }, { value: "generic", label: "Generic" }], 'data-action="toggle-generic-template"')} ${valueField("st-whtimeout", "超时秒数", settings.webhookTimeout || 30, "number", 'min="1"')}</div><div class="form-grid"><div class="field"><label class="field-label" for="st-whurl">Webhook 地址 ${settings.webhookUrl ? '<span class="badge ok">已设置</span>' : ""}</label><input id="st-whurl" type="text" placeholder="${settings.webhookUrl ? "（已设置，留空不变）" : "https://…"}"></div><div class="field"><label class="field-label" for="st-whsec">Webhook 签名密钥 ${settings.webhookSecret ? '<span class="badge ok">已设置</span>' : ""}</label><input id="st-whsec" type="password" placeholder="${settings.webhookSecret ? "（已设置，留空不变）" : ""}"></div></div><div id="st-whtpl-box" ${settings.webhookType === "generic" ? "" : "hidden"}><label class="field-label" for="st-whtpl">generic 自定义模板（JSON，{text} 为消息占位符）</label><textarea id="st-whtpl">${esc(settings.webhookTemplate || "")}</textarea></div></div>
    <button class="panel-toggle" type="button" data-action="toggle-panel" data-panel="panel-smtp" aria-expanded="false" aria-controls="panel-smtp"><span class="panel-arrow" id="arrow-smtp">▸</span><span class="panel-label">SMTP 邮件通知</span><span class="badge ${settings.smtpEnabled ? "ok" : "muted"}">${settings.smtpEnabled ? "已启用" : "已禁用"}</span></button><div id="panel-smtp" class="panel-body" hidden>${switchControl("st-smtp-enabled", "启用 SMTP", "发送运行状态到邮箱", settings.smtpEnabled, "toggle-notify-flag", 'data-flag="st-smtp-enabled"')}<div class="form-grid three">${valueField("st-host", "SMTP 服务器", settings.smtpHost)}${valueField("st-port2", "端口", settings.smtpPort, "number")}${selectField("st-secure", "加密方式", settings.smtpSecure, ["auto", "ssl", "starttls", "none"])}</div><div class="form-grid">${valueField("st-user", "账号", settings.smtpUser)}<div class="field"><label class="field-label" for="st-pwd">授权码 ${settings.smtpPassword ? '<span class="badge ok">已设置</span>' : ""}</label><input id="st-pwd" type="password" placeholder="${settings.smtpPassword ? "（已设置，留空不变）" : ""}"></div></div><div class="form-grid">${valueField("st-to", "收件人（逗号分隔）", settings.smtpTo)}${valueField("st-from", "发件人显示地址（留空=账号）", settings.smtpFrom)}</div><div class="form-grid">${valueField("st-subject", "主题前缀", settings.smtpSubjectPrefix)}${valueField("st-smtp-timeout", "超时秒数", settings.smtpTimeout || 30, "number", 'min="1"')}</div></div><div class="modal-footer-inline plain"><button type="button" data-action="save-notify-settings">保存设置</button><button class="ghost" type="button" data-action="test-notify">测试通知</button></div></section>`;
  return body.replaceAll("▾", icon("chevronDown", "icon panel-arrow-icon")).replaceAll("▸", icon("chevronRight", "icon panel-arrow-icon"));
}

/** 更新区：设置（自动检查/渠道/镜像源）与检查 / 下载 / 应用状态区。 */
function updateSectionMarkup(settings) {
  return `<section class="card section-surface update-section" data-testid="update-section"><div class="section-heading"><div><h3>更新</h3><span class="muted">启动时自动检查一次新版本；下载与应用均为手动操作，应用前会等待任务与编辑会话结束。</span></div></div>
    <div class="settings-list">${switchControl("st-update-check", "自动检查更新", "服务启动时检查一次（不会自动下载）", settings.updateCheckEnabled, "toggle-update-flag", 'data-flag="st-update-check"')}</div>
    <div class="form-grid">${selectField("st-update-channel", "更新渠道", settings.updateChannel, [{ value: "prerelease", label: "预发布（Pre-release）" }, { value: "stable", label: "稳定版" }])}${valueField("st-update-source", "镜像源地址", settings.updateSourceUrl, "text", 'placeholder="留空 = 默认 GitHub"')}</div>
    <div class="modal-footer-inline plain"><button type="button" data-action="update-save-settings" data-testid="update-save-settings">保存更新设置</button></div>
    <div id="update-status-box" class="update-status" data-testid="update-status"></div>
  </section>`;
}

let updateStatus = null;

async function loadUpdateStatus() {
  try {
    const data = await api("GET", "/api/update/status");
    updateStatus = data;
    renderUpdateStatus(data);
  } catch { /* 状态区保持占位 */ }
}

/** 状态区渲染：当前版本 / 渠道 / 最新版本与 release note（截断）/ 进度 / 按钮流。 */
function renderUpdateStatus(data) {
  const box = $("#update-status-box");
  if (!box) return;
  if (!data) {
    box.innerHTML = '<p class="muted helper-copy">更新状态加载中...</p>';
    return;
  }
  const state = data.state || "idle";
  const current = data.current || "—";
  const channelText = data.channel === "stable" ? "稳定版" : "预发布（Pre-release）";
  let actions = `<button type="button" data-action="update-check" data-testid="update-check">检查更新</button>`;
  if (data.available && state === "idle") {
    actions += ` <button type="button" class="primary" data-action="update-download" data-testid="update-download">下载更新 v${esc(data.latest)}</button>`;
  }
  if (state === "downloading") {
    actions += ` <button type="button" class="ghost" data-action="update-cancel" data-testid="update-cancel">取消下载</button>`;
  }
  if (state === "ready") {
    actions += ` <button type="button" class="primary" data-action="update-apply" data-testid="update-apply">立即更新</button> <button type="button" class="ghost" data-action="update-defer" data-testid="update-defer">下次启动更新</button>`;
  }
  let progress = "";
  if (state === "downloading" && typeof data.progress === "number") {
    progress = `<div class="progress-line" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${data.progress}" aria-label="下载进度"><div data-progress="${data.progress}"></div></div>`;
  }
  let notes = "";
  if (data.notes) {
    const text = data.notes.length > 300 ? data.notes.slice(0, 300) + "…" : data.notes;
    notes = `<p class="update-notes">${esc(text)}</p>`;
  }
  let stateText = "";
  if (state === "checking") stateText = '<p class="muted helper-copy">正在检查更新...</p>';
  else if (state === "downloading") stateText = '<p class="muted helper-copy">正在下载并校验更新包...</p>';
  else if (state === "ready") stateText = '<p class="muted helper-copy">更新已就绪，应用前请确认没有正在运行的任务。</p>';
  else if (state === "applying") stateText = '<p class="muted helper-copy">正在应用更新，服务即将重启...</p>';
  else if (state === "idle" && data.available) stateText = `<p class="muted helper-copy">发现新版本 v${esc(data.latest)}${data.prerelease ? "（Pre-release）" : ""}。</p>`;
  else if (state === "idle" && !data.available && data.error) stateText = `<p class="callout callout-warning">检查失败：${esc(data.error)}</p>`;
  else if (state === "idle") stateText = '<p class="muted helper-copy">当前已是最新版本。</p>';
  box.innerHTML = `<div class="detail"><div class="kv"><span class="k">当前版本</span><span>v${esc(current)}</span></div><div class="kv"><span class="k">更新渠道</span><span>${channelText}</span></div></div>${notes}${stateText}${progress}<div class="modal-footer-inline plain update-actions">${actions}</div>`;
  box.querySelectorAll("[data-progress]").forEach(element => {
    element.style.width = `${Math.max(0, Math.min(100, Number(element.dataset.progress) || 0))}%`;
  });
}

function updateSettingsPayload() {
  return {
    updateCheckEnabled: $("#st-update-check")?.getAttribute("aria-pressed") === "true",
    updateChannel: $("#st-update-channel")?.value || "prerelease",
    updateSourceUrl: ($("#st-update-source")?.value || "").trim(),
  };
}

async function saveUpdateSettings() {
  try {
    await api("PUT", "/api/settings", updateSettingsPayload());
    toast("更新设置已保存");
    await loadUpdateStatus();
  } catch (error) { toast(error.message, "error"); }
}

async function checkUpdate() {
  try {
    const result = await api("POST", "/api/update/check");
    renderUpdateStatus(result);
    if (result.available) toast(`发现新版本 v${result.latest}`);
    else toast("当前已是最新版本", "info");
  } catch (error) { toast(error.message, "error"); }
}

async function startUpdateDownload() {
  try {
    await api("POST", "/api/update/download");
    scheduleUpdateStatusPoll();
  } catch (error) { toast(error.message, "error"); }
}

function scheduleUpdateStatusPoll() {
  schedule(async () => {
    try {
      const status = await api("GET", "/api/update/status");
      if (!isCurrent("settings", state.routeToken)) return;
      renderUpdateStatus(status);
      if (status.state !== "downloading") return;
      scheduleUpdateStatusPoll();
    } catch { /* 服务可能已重启，停止轮询 */ }
  }, 1000, "settings", state.routeToken);
}

async function cancelUpdateDownload() {
  try {
    await api("POST", "/api/update/cancel");
    toast("下载已取消");
    await loadUpdateStatus();
  } catch (error) { toast(error.message, "error"); }
}

async function applyUpdate(defer) {
  try {
    const result = await api("POST", "/api/update/apply", { defer });
    if (result.error) {
      toast(result.error, "error");
      if (result.code === "busy") toast("可先等待任务结束，或选择「下次启动更新」", "info");
      await loadUpdateStatus();
      return;
    }
    if (result.deferred) {
      toast("已登记：下次启动服务时自动应用");
      await loadUpdateStatus();
      return;
    }
    renderUpdateStatus({ ...(updateStatus || {}), state: "applying" });
    showModal(modalShell("正在应用更新", '<p class="modal-copy">更新已开始，服务即将重启并自动恢复，页面连接会短暂中断...</p>'), false, true);
    pollServiceRestart(Date.now() + 120000);
  } catch (error) { toast(error.message, "error"); }
}

/** 更新应用后轮询服务恢复（相对路径探测当前源），恢复后刷新页面。 */
function pollServiceRestart(deadline) {
  schedule(async () => {
    try {
      const probe = await fetch("api/status", { cache: "no-store" });
      if (probe.ok) {
        location.reload();
        return;
      }
    } catch { /* 服务未就绪 */ }
    if (Date.now() < deadline) pollServiceRestart(deadline);
    else {
      closeModal();
      toast("服务重启超时，请手动刷新页面", "error");
    }
  }, 1000, "settings", state.routeToken);
}

function togglePanel(panelId, trigger) {
  const panel = $("#" + panelId); if (!panel) return;
  const hidden = panel.hasAttribute("hidden");
  panel.toggleAttribute("hidden", !hidden);
  if (trigger) {
    trigger.setAttribute("aria-expanded", String(hidden));
    const arrow = trigger.querySelector(".panel-arrow");
    if (arrow) arrow.innerHTML = icon(hidden ? "chevronDown" : "chevronRight", "icon panel-arrow-icon");
  }
}

function toggleGenericTemplate() {
  const box = $("#st-whtpl-box"); if (!box) return;
  box.toggleAttribute("hidden", $("#st-whtype")?.value !== "generic");
}

async function saveNotifySettings() {
  const payload = {
    webhookEnabled: $("#st-wh-enabled")?.getAttribute("aria-pressed") === "true",
    smtpEnabled: $("#st-smtp-enabled")?.getAttribute("aria-pressed") === "true",
    webhookType: $("#st-whtype")?.value || "generic",
    webhookTimeout: +($("#st-whtimeout")?.value || 30),
    webhookTemplate: $("#st-whtpl")?.value.trim() || "",
    smtpHost: $("#st-host")?.value.trim() || "",
    smtpPort: +($("#st-port2")?.value || 465),
    smtpSecure: $("#st-secure")?.value || "auto",
    smtpUser: $("#st-user")?.value.trim() || "",
    smtpTo: $("#st-to")?.value.trim() || "",
    smtpFrom: $("#st-from")?.value.trim() || "",
    smtpSubjectPrefix: $("#st-subject")?.value.trim() || "",
    smtpTimeout: +($("#st-smtp-timeout")?.value || 30),
  };
  const secrets = [
    ["webhookUrl", $("#st-whurl")?.value.trim() || ""],
    ["webhookSecret", $("#st-whsec")?.value.trim() || ""],
    ["smtpPassword", $("#st-pwd")?.value.trim() || ""],
  ].filter(([, value]) => value.length > 0);
  try {
    await api("PUT", "/api/settings", payload);
    for (const [key, value] of secrets) await api("PUT", "/api/settings", { secretKey: key, secretValue: value });
    toast(secrets.length ? "通知设置与密钥已保存" : "通知设置已保存");
    await pageSettings(state.routeToken);
  } catch (error) { toast(error.message, "error"); }
}

async function testNotify() {
  try {
    const result = await api("POST", "/api/settings/test");
    toast(result.ok ? "测试通知发送成功" : "发送失败，详见日志", result.ok ? "info" : "error");
  } catch (error) { toast(error.message, "error"); }
}

export function markRestartRequired() {
  restartRequired = true;
  if (state.page !== "settings") return;
  const view = document.querySelector("#view");
  const lightweight = $("#st-lightweight")?.getAttribute("aria-pressed") === "true";
  const markup = restartNoticeMarkup({ ...(state.settings || {}), lightweightMode: lightweight });
  const existing = document.querySelector("#restart-notice");
  if (existing) {
    existing.outerHTML = markup;
    return;
  }
  if (!view) return;
  const header = view.querySelector(".page-head");
  header?.insertAdjacentHTML("afterend", markup);
}

/** 自动保存串行链（用户需求：修改一次即保存一次，成功静默、失败 toast）：连续触发（快速切换开关）
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

/** 设置页控件自动保存绑定：输入/下拉失焦（change）即保存；切换按钮经 toggle-st-flag 触发。 */
function bindAutoSave() {
  ["st-loglevel", "st-retention", "st-port", "st-token"].forEach(id => {
    $("#" + id)?.addEventListener("change", () => {
      if (id === "st-port") markRestartRequired();
      autoSave();
    });
  });
}

/** 重启服务：等待挂起的自动保存完成后弹确认卡片（端口改动已即时保存，无需再校验）。 */
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
    // 存储不可用（隐私模式）时按无令牌处理，避免 getItem 抛异常中断重启轮询。
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
    if (target.dataset.restartRequired === "true" || ["st-lightweight", "st-remote"].includes(target.dataset.flag)) markRestartRequired();
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
  "toggle-panel": target => togglePanel(target.dataset.panel, target),
  "toggle-generic-template": () => toggleGenericTemplate(),
  "toggle-notify-flag": target => {
    const btn = $("#" + target.dataset.flag);
    if (btn) btn.setAttribute("aria-pressed", btn.getAttribute("aria-pressed") === "true" ? "false" : "true");
  },
  "save-notify-settings": target => withBusy(target, () => saveNotifySettings()),
  "test-notify": target => withBusy(target, () => testNotify()),
  "update-check": target => withBusy(target, () => checkUpdate()),
  "update-download": target => withBusy(target, () => startUpdateDownload()),
  "update-cancel": () => cancelUpdateDownload(),
  "update-apply": target => withBusy(target, () => applyUpdate(false)),
  "update-defer": () => applyUpdate(true),
  "update-save-settings": target => withBusy(target, () => saveUpdateSettings()),
  "toggle-update-flag": target => {
    const btn = $("#" + target.dataset.flag);
    if (btn) btn.setAttribute("aria-pressed", btn.getAttribute("aria-pressed") === "true" ? "false" : "true");
  },
};
