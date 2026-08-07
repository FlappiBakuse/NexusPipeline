import { api, $, $$, esc, finalStatusOf, fmtTime, statusBadge } from "../core/api.js";
import { isCurrent, schedule, state } from "../core/state.js";
import { closeModal, modalShell, navActive, render, setTopbarTitle, showModal, toast } from "../core/ui.js";

function runningMarkup(running) {
  if (!running.length) return '<div class="empty"><strong>当前没有正在运行的任务</strong>选择脚本或队列后，可以在这里查看实时状态。</div>';
  return running.map(record => `<article class="list-item running-item">
    <div class="list-item-head"><div><div class="list-item-title"><strong>${esc(record.targetName)}</strong><span class="badge ${record.kind === "queue" ? "blue" : "muted"}">${record.kind === "queue" ? "调度队列" : "脚本实例"}</span><span class="badge muted">${record.mode === "auto" ? "自动" : "手动"}</span>${record.kind === "queue" ? `<span class="muted">${record.doneTasks}/${record.totalTasks} 项</span>` : ""}</div></div><button class="sm danger" type="button" data-action="cancel-run" data-id="${record.id}">取消</button></div>
    <div class="qk-row">当前：${esc(record.currentScriptName || "-")} ${esc(record.currentStatus || "")} · 第 ${record.currentAttempt}/${record.currentMaxAttempts} 次</div>
    <div class="progress-line"><div data-progress="${record.kind === "queue" && record.totalTasks ? Math.round(record.doneTasks / record.totalTasks * 100) : record.currentAttempt ? Math.round(record.currentAttempt / record.currentMaxAttempts * 100) : 0}"></div></div>
    <pre class="logbox run-log">${esc((record.logTail || []).join("\n")) || "(暂无日志输出)"}</pre>
  </article>`).join("");
}

function applyProgress(root = document) {
  root.querySelectorAll("[data-progress]").forEach(element => {
    element.style.width = `${Math.max(0, Math.min(100, Number(element.dataset.progress) || 0))}%`;
  });
}

function updateRunning(status) {
  const panel = $("#dispatch-running");
  if (!panel) return;
  const running = status.running || [];
  panel.innerHTML = `<div class="section-heading"><h3>正在运行（${running.length}）</h3><span class="muted">每 2 秒更新</span></div>${runningMarkup(running)}`;
  applyProgress(panel);
  $$(".run-log", panel).forEach(log => { log.scrollTop = log.scrollHeight; });
}

export async function pageDispatch(token) {
  if (!isCurrent("dispatch", token)) return;
  navActive("dispatch"); setTopbarTitle("调度中心");
  let status, scripts, queues;
  try { [status, scripts, queues] = await Promise.all([api("GET", "/api/status"), api("GET", "/api/scripts"), api("GET", "/api/queues")]); }
  catch (error) { render(`<div class="empty"><strong>加载调度中心失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("dispatch", token)) return;
  state.scripts = scripts; state.queues = queues;
  render(`<div class="page-head"><div><div class="eyebrow">RUN CONTROL</div><h2>调度中心</h2><p class="page-kicker">手动启动任务，观察实时输出并及时取消运行。</p></div></div>
    <section class="card" id="dispatch-running" data-testid="dispatch-running"><div class="section-heading"><h3>正在运行（${(status.running || []).length}）</h3><span class="muted">每 2 秒更新</span></div>${runningMarkup(status.running || [])}</section>
    <section class="card"><div class="section-heading"><h3>手动执行脚本实例</h3><span class="muted">可选择用户配置</span></div><div class="form-grid dispatch-controls dispatch-script-controls"><div><label class="field-label" for="dc-script">脚本实例</label><select id="dc-script" data-testid="dispatch-script"><option value="">（选择脚本实例）</option>${scripts.map(script => `<option value="${script.id}">${esc(script.name)}</option>`).join("")}</select></div><div><label class="field-label" for="dc-user">用户配置</label><select id="dc-user"><option value="">（不使用用户配置）</option></select></div><div class="control-action"><button type="button" data-action="dispatch-script">执行</button></div></div></section>
    <section class="card"><div class="section-heading"><h3>手动执行调度队列</h3><span class="muted">按队列内顺序运行</span></div><div class="form-grid dispatch-controls dispatch-queue-controls"><div><label class="field-label" for="dc-queue">调度队列</label><select id="dc-queue"><option value="">（选择调度队列）</option>${queues.map(queue => `<option value="${queue.id}">${esc(queue.name)}</option>`).join("")}</select></div><div class="control-action"><button type="button" data-action="dispatch-queue">执行</button></div></div></section>`);
  applyProgress();
  $("#dc-script")?.addEventListener("change", event => refreshUserSelect(event.target.value));
  schedule(() => refreshDispatch(token), 2000, "dispatch", token);
}

async function refreshDispatch(token) {
  if (!isCurrent("dispatch", token)) return;
  try { const status = await api("GET", "/api/status"); if (isCurrent("dispatch", token)) updateRunning(status); }
  catch (error) { if (isCurrent("dispatch", token)) toast("状态更新失败：" + error.message, "error"); }
  schedule(() => refreshDispatch(token), 2000, "dispatch", token);
}

function refreshUserSelect(scriptId) {
  const select = $("#dc-user");
  if (!select) return;
  const script = state.scripts.find(item => item.id === scriptId);
  select.innerHTML = '<option value="">（不使用用户配置）</option>' + ((script?.users || []).filter(user => user.enabled).map(user => `<option value="${esc(user.name)}">${esc(user.name)}</option>`).join(""));
}

export async function dispatchScript() {
  const id = $("#dc-script")?.value;
  if (!id) { toast("请选择脚本实例", "error"); return; }
  try { await api("POST", "/api/dispatch/script", { scriptId: id, mode: "manual", userName: $("#dc-user")?.value || "" }); toast("已开始执行"); }
  catch (error) { toast(error.message, "error"); }
}

export async function dispatchQueue() {
  const id = $("#dc-queue")?.value;
  if (!id) { toast("请选择调度队列", "error"); return; }
  try { await api("POST", "/api/dispatch/queue", { queueId: id, mode: "manual" }); toast("已开始执行"); }
  catch (error) { toast(error.message, "error"); }
}

export async function cancelRun(runId) {
  try { await api("POST", "/api/cancel", { runId }); toast("已发送取消请求"); }
  catch (error) { toast(error.message, "error"); }
}

export async function pageHistory(token) {
  if (!isCurrent("history", token)) return;
  navActive("history"); setTopbarTitle("历史记录");
  let records, scripts, queues;
  try { [records, scripts, queues] = await Promise.all([api("GET", "/api/history?days=7"), api("GET", "/api/scripts"), api("GET", "/api/queues")]); }
  catch (error) { render(`<div class="empty"><strong>加载历史记录失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("history", token)) return;
  const scriptName = id => scripts.find(script => script.id === id)?.name || "(已删除)";
  const queueName = id => queues.find(queue => queue.id === id)?.name || "";
  const content = records.length ? `<section class="card"><div class="table-scroll"><table class="data-table"><thead><tr><th>时间</th><th>脚本</th><th>队列</th><th>模式</th><th>结果</th><th>详情</th></tr></thead><tbody>${records.map(record => `<tr><td>${esc(fmtTime(record.startTime))}</td><td>${esc(scriptName(record.scriptInstanceId))}</td><td>${esc(queueName(record.queueId)) || "-"}</td><td>${record.mode === "auto" ? "自动" : "手动"}</td><td>${statusBadge(finalStatusOf(record))}</td><td class="ops">${esc(record.resultDetail)}<br><button class="sm" type="button" data-action="history-detail" data-id="${record.id}">查看详情</button></td></tr>`).join("")}</tbody></table></div></section>` : '<div class="empty"><strong>暂无历史记录</strong>运行脚本或调度队列后在此查看。</div>';
  render(`<div class="page-head"><div><div class="eyebrow">RUN ARCHIVE</div><h2>历史记录（最近 7 天，共 ${records.length} 条）</h2><p class="page-kicker">按运行时间查看结果、重试过程和脚本输出。</p></div></div>${content}`);
}

export async function historyDetail(id) {
  try {
    const data = await api("GET", "/api/history/detail?id=" + encodeURIComponent(id));
    const record = data.record;
    if (!record) return;
    const attempts = (record.attemptDetails || []).map(attempt => `<div class="subsection"><h3>第 ${attempt.number} 次尝试：${attempt.status === "success" ? "成功" : attempt.status === "cancelled" ? "已取消" : "失败"}</h3><div class="detail"><div class="kv"><span class="k">原因</span><span>${esc(attempt.reason || "-")}</span></div><div class="kv"><span class="k">时间</span><span>${esc(fmtTime(attempt.startTime))} - ${esc(fmtTime(attempt.endTime))}</span></div></div>${attempt.outputTail ? `<div class="qk-row">控制台输出</div><pre class="logbox">${esc(attempt.outputTail.split("\n").slice(-12).join("\n"))}</pre>` : ""}</div>`).join("");
    const body = `<div class="detail"><div class="kv"><span class="k">开始</span><span>${esc(fmtTime(record.startTime))}</span></div><div class="kv"><span class="k">结束</span><span>${esc(fmtTime(record.endTime))}</span></div><div class="kv"><span class="k">模式</span><span>${record.mode === "auto" ? "自动运行" : "手动运行"}</span></div>${record.userName ? `<div class="kv"><span class="k">用户</span><span>${esc(record.userName)}</span></div>` : ""}<div class="kv"><span class="k">重试</span><span>${record.attemptCount || record.attempts} / ${record.maxAttempts || "-"} 次</span></div><div class="kv"><span class="k">结果</span><span>${statusBadge(finalStatusOf(record))} ${esc(record.resultDetail)}</span></div></div>${attempts}<div class="subsection"><h3>脚本日志（${data.logTotalLines || 0} 行${(data.logTotalLines || 0) > 200 ? "，仅显示尾部 200 行" : ""}）</h3><pre class="logbox">${esc(data.logTail || "（无脚本日志）")}</pre></div>`;
    showModal(modalShell(`${esc(record.scriptName)} 运行详情`, body, '<button class="ghost" type="button" data-action="close-modal">关闭</button>'), true);
  } catch (error) { toast(error.message, "error"); }
}

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

function valueField(id, label, value, type = "text", extra = "") { return `<div><label class="field-label" for="${id}">${label}</label><input id="${id}" type="${type}" value="${esc(value)}" ${extra}></div>`; }
function selectField(id, label, value, options) { return `<div><label class="field-label" for="${id}">${label}</label><select id="${id}">${options.map(option => `<option value="${option}" ${option === value ? "selected" : ""}>${option}</option>`).join("")}</select></div>`; }

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

export async function pageSettings(token) {
  if (!isCurrent("settings", token)) return;
  navActive("settings"); setTopbarTitle("设置");
  let data;
  try { data = await api("GET", "/api/settings"); }
  catch (error) { render(`<div class="empty"><strong>加载设置失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("settings", token)) return;
  state.settings = data.settings;
  const settings = data.settings;
  render(`<div class="page-head"><div><div class="eyebrow">SYSTEM PREFERENCES</div><h2>设置</h2><p class="page-kicker">控制服务启动方式、历史保留和本地 Web 服务端口。</p></div></div><section class="card"><div class="section-heading"><h3>服务行为</h3><span class="muted">部分改动需重启生效</span></div><label class="check"><input id="st-autostart" type="checkbox" ${settings.autoStart ? "checked" : ""}><span>开机自启动（注册到当前用户启动项）</span></label><label class="check"><input id="st-lightweight" type="checkbox" ${settings.lightweightMode ? "checked" : ""}><span>轻量运行模式（不启动网页服务，重启生效）</span></label><label class="check"><input id="st-browser" type="checkbox" ${settings.autoOpenBrowser ? "checked" : ""}><span>服务启动后自动打开浏览器</span></label><div class="form-grid">${valueField("st-retention", "历史保留天数", settings.historyRetentionDays, "number", 'min="1"')}${valueField("st-port", "Web 端口（重启生效）", settings.webPort, "number", 'min="1024" max="65535"')}${selectField("st-loglevel", "日志级别", settings.logLevel || "info", ["debug", "info", "warn", "error", "fatal"])}</div><p class="muted helper-copy">日志级别：DEBUG 全部记录（含 Web 请求）；INFO 常规；WARN 仅警告与错误；ERROR 仅错误与致命；FATAL 仅致命错误。即时生效。</p><div class="modal-footer-inline"><button type="button" data-action="save-settings">保存设置</button></div></section><div class="helper-copy muted">通知渠道（Webhook / SMTP）请在「插件」页的通知推送插件配置中设置。</div>`);
}

export async function saveSettings() {
  const payload = { autoStart: $("#st-autostart")?.checked || false, lightweightMode: $("#st-lightweight")?.checked || false, autoOpenBrowser: $("#st-browser")?.checked || false, historyRetentionDays: +($("#st-retention")?.value || 3), webPort: +($("#st-port")?.value || 58731), logLevel: $("#st-loglevel")?.value || "info" };
  try { await api("PUT", "/api/settings", payload); toast("设置已保存"); await pageSettings(state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}
