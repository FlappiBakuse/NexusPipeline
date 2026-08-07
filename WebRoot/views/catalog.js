import { api, $, $$, actionLabel, dayDesc, esc } from "../core/api.js";
import { isCurrent, schedule, state } from "../core/state.js";
import { closeModal, modalShell, navActive, render, setTopbarTitle, showModal, toast } from "../core/ui.js";

function pageHeader(kicker, title, description, action = "") {
  return `<div class="page-head"><div><div class="eyebrow">${kicker}</div><h2>${title}</h2>${description ? `<p class="page-kicker">${description}</p>` : ""}</div>${action}</div>`;
}

function valueField(id, label, value, type = "text", extra = "") {
  return `<div><label class="field-label" for="${id}">${label}</label><input id="${id}" type="${type}" value="${esc(value)}" ${extra}></div>`;
}

export async function pageScripts(token) {
  if (!isCurrent("scripts", token)) return;
  navActive("scripts");
  setTopbarTitle("脚本实例");
  let scripts;
  try {
    scripts = await api("GET", "/api/scripts");
  } catch (error) {
    if (isCurrent("scripts", token)) render(`<div class="empty"><strong>加载脚本实例失败</strong>${esc(error.message)}</div>`);
    return;
  }
  if (!isCurrent("scripts", token)) return;
  state.scripts = scripts;
  const action = '<button type="button" data-action="open-script-modal" data-testid="new-script">新建脚本实例</button>';
  const content = scripts.length === 0
    ? '<div class="empty"><strong>暂无脚本实例</strong>点击右上角「新建脚本实例」创建你的第一个脚本。</div>'
    : `<section class="card"><div class="table-scroll"><table class="data-table"><thead><tr><th>名称</th><th>主程序</th><th>日志路径</th><th>游戏</th><th>重试</th><th>通知</th><th>操作</th></tr></thead><tbody>
      ${scripts.map(script => `<tr>
        <td><strong>${esc(script.name)}</strong></td>
        <td class="mono" title="${esc(script.mainExe)}">${esc(script.mainExe)}</td>
        <td class="mono" title="${esc(script.logPath)}">${esc(script.logPath) || "-"}</td>
        <td>${script.launchGame ? esc(script.gameExe || "?") : '<span class="muted">不启动</span>'}</td>
        <td>${script.maxAttempts}</td>
        <td>${script.notifyEnabled ? '<span class="badge ok">开</span>' : '<span class="badge muted">关</span>'}</td>
        <td class="ops"><button class="sm" type="button" data-action="edit-script" data-id="${script.id}">编辑脚本</button><button class="sm" type="button" data-action="manage-users" data-id="${script.id}">用户管理${(script.users || []).length ? `（${script.users.length}）` : ""}</button><button class="sm danger" type="button" data-action="delete-script" data-id="${script.id}" data-name="${esc(script.name)}">删除脚本</button></td>
      </tr>`).join("")}
    </tbody></table></div></section>`;
  render(pageHeader("SCRIPT CATALOG", "脚本实例", "管理脚本入口、用户配置和运行策略。", action) + content);
}

export async function openScriptModal(id = "") {
  let script = id ? state.scripts.find(item => item.id === id) : null;
  if (id && !script) {
    try {
      state.scripts = await api("GET", "/api/scripts");
      script = state.scripts.find(item => item.id === id);
    } catch (error) {
      toast("加载脚本失败：" + error.message, "error");
      return;
    }
  }
  const value = script || {};
  state.scriptDraft = {
    id: value.id || "", name: value.name || "", rootPath: value.rootPath || "", mainExe: value.mainExe || "",
    args: value.args || "", configPath: value.configPath || "", logPath: value.logPath || "",
    launchGame: !!value.launchGame, gameExe: value.gameExe || "", gameArgs: value.gameArgs || "",
    gameWaitSeconds: value.gameWaitSeconds ?? 30, forceCloseGame: !!value.forceCloseGame,
    maxAttempts: value.maxAttempts ?? 3, logStallTimeoutMinutes: value.logStallTimeoutMinutes ?? 5,
    totalTimeoutMinutes: value.totalTimeoutMinutes ?? 120, successMarkers: value.successMarkers || "",
    notifyEnabled: !!value.notifyEnabled,
  };
  const d = state.scriptDraft;
  const body = `<div class="form-grid">
    ${valueField("sm-name", "脚本名称 <span class='req'>*</span>", d.name)}
    ${valueField("sm-root", "脚本根目录 <span class='req'>*</span>", d.rootPath, "text", 'placeholder="例如 C:\\Scripts\\Daily"')}
  </div>
  <div class="form-grid">
    ${valueField("sm-exe", "脚本主程序路径 <span class='req'>*</span>", d.mainExe, "text", 'placeholder="请先填写脚本根目录"')}
    ${valueField("sm-args", "脚本自启动参数", d.args, "text", 'placeholder="可选"')}
  </div>
  <div class="form-grid">
    ${valueField("sm-config", "配置文件路径/文件夹 <span class='req'>*</span>", d.configPath, "text", 'placeholder="请先填写脚本根目录"')}
    ${valueField("sm-log", "日志文件夹路径 <span class='req'>*</span>", d.logPath, "text", 'placeholder="请先填写脚本根目录"')}
  </div>
  <div class="subsection"><div class="section-heading"><h3>游戏与通知</h3><span class="muted">按需启用，不影响基础脚本执行</span></div>
    <div class="check-grid">
      <label class="check"><input id="sm-launch" type="checkbox" ${d.launchGame ? "checked" : ""}><span>运行脚本前启动游戏</span></label>
      <label class="check"><input id="sm-force" type="checkbox" ${d.forceCloseGame ? "checked" : ""}><span>运行结束后强制关闭游戏</span></label>
      <label class="check"><input id="sm-notify" type="checkbox" ${d.notifyEnabled ? "checked" : ""}><span>发送运行状态通知</span></label>
    </div>
    <div id="sm-game-box" class="nested-panel" ${d.launchGame ? "" : "hidden"}>
      <div class="form-grid">${valueField("sm-game-exe", "游戏路径", d.gameExe)}${valueField("sm-game-args", "启动参数", d.gameArgs)}</div>
      <div class="form-grid single-narrow">${valueField("sm-game-wait", "启动后等待秒数", d.gameWaitSeconds, "number", 'min="0"')}</div>
    </div>
  </div>
  <div class="subsection"><div class="section-heading"><h3>运行设置</h3><span class="muted">超时后会按最大尝试次数重试</span></div>
    <div class="form-grid three">
      ${valueField("sm-attempts", "最大尝试次数（含首次） <span class='req'>*</span>", d.maxAttempts, "number", 'min="1"')}
      ${valueField("sm-stall", "日志无更新超时（分钟） <span class='req'>*</span>", d.logStallTimeoutMinutes, "number", 'min="1"')}
      ${valueField("sm-total", "运行总时间超时（分钟） <span class='req'>*</span>", d.totalTimeoutMinutes, "number", 'min="1"')}
    </div>
    <label class="field-label" for="sm-markers">自定义完成标志（逗号分隔，留空=内置关键词）</label><input id="sm-markers" type="text" value="${esc(d.successMarkers)}">
  </div>`;
  const footer = '<button type="button" data-action="save-script">保存</button><button class="ghost" type="button" data-action="close-modal">取消</button>';
  showModal(modalShell(id ? "编辑脚本实例" : "新建脚本实例", body, footer));
  syncScriptGhostState();
  $("#sm-launch")?.addEventListener("change", event => {
    const box = $("#sm-game-box");
    if (box) box.toggleAttribute("hidden", !event.target.checked);
  });
  const rootInput = $("#sm-root");
  rootInput?.addEventListener("input", syncScriptGhostState);
  rootInput?.addEventListener("change", syncScriptGhostState);
  rootInput?.addEventListener("keyup", syncScriptGhostState);
}

export function syncScriptGhostState() {
  const root = $("#sm-root");
  const hasRoot = !!(root && root.value.trim());
  ["sm-exe", "sm-args", "sm-config", "sm-log"].forEach(id => {
    const element = $("#" + id);
    if (element) element.disabled = !hasRoot;
  });
}

export async function saveScript() {
  const required = [["sm-name", "脚本名称"], ["sm-root", "脚本根目录"], ["sm-exe", "脚本主程序路径"], ["sm-config", "配置文件路径"], ["sm-log", "日志文件夹路径"]];
  for (const [id, label] of required) {
    const element = $("#" + id);
    if (!element?.value.trim()) {
      toast("请填写" + label, "error");
      element?.classList.add("field-error");
      element?.focus();
      return;
    }
    element.classList.remove("field-error");
  }
  const attempts = parseInt($("#sm-attempts")?.value, 10);
  const stall = parseInt($("#sm-stall")?.value, 10);
  const total = parseInt($("#sm-total")?.value, 10);
  if (!(attempts >= 1) || !(stall >= 1) || !(total >= 1)) {
    toast("运行设置中的次数和超时必须为正数", "error");
    return;
  }
  const payload = {
    id: state.scriptDraft.id, name: $("#sm-name").value.trim(), rootPath: $("#sm-root").value.trim(),
    mainExe: $("#sm-exe").value.trim(), args: $("#sm-args").value.trim(), configPath: $("#sm-config").value.trim(), logPath: $("#sm-log").value.trim(),
    launchGame: $("#sm-launch").checked, gameExe: $("#sm-game-exe")?.value.trim() || "", gameArgs: $("#sm-game-args")?.value.trim() || "", gameWaitSeconds: +($("#sm-game-wait")?.value || 0) || 0,
    forceCloseGame: $("#sm-force").checked, maxAttempts: attempts, logStallTimeoutMinutes: stall, totalTimeoutMinutes: total,
    successMarkers: $("#sm-markers").value.trim(), notifyEnabled: $("#sm-notify").checked,
  };
  try {
    if (payload.id) await api("PUT", "/api/scripts/" + payload.id, payload);
    else await api("POST", "/api/scripts", payload);
    closeModal();
    toast("脚本实例已保存");
    const token = state.routeToken;
    await pageScripts(token);
  } catch (error) { toast(error.message, "error"); }
}

export async function deleteScript(id, name) {
  if (!confirm("确定删除脚本实例「" + name + "」？")) return;
  try { await api("DELETE", "/api/scripts/" + id); toast("脚本实例已删除"); await pageScripts(state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export async function pageScriptUsers(scriptId, token) {
  const page = "scripts/" + scriptId + "/users";
  if (!isCurrent(page, token)) return;
  navActive("scripts");
  setTopbarTitle("用户管理");
  let scripts;
  try { scripts = await api("GET", "/api/scripts"); }
  catch (error) { render(`<div class="empty"><strong>加载用户失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent(page, token)) return;
  state.scripts = scripts;
  const script = scripts.find(item => item.id === scriptId);
  if (!script) { render('<div class="empty"><strong>脚本实例不存在</strong><a class="back-link" href="#/scripts">返回脚本实例</a></div>'); return; }
  const users = script.users || [];
  const usersMarkup = users.length ? users.map(user => `<article class="card user-card">
    <div class="list-item-head"><div><div class="list-item-title"><strong>${esc(user.name)}</strong>${user.enabled ? '<span class="badge ok">已启用</span>' : '<span class="badge muted">已禁用</span>'}</div></div>
      <div class="action-row"><button class="sm" type="button" data-action="edit-user-config" data-id="${script.id}" data-name="${esc(user.name)}">编辑配置</button><button class="sm" type="button" data-action="edit-user" data-id="${script.id}" data-name="${esc(user.name)}">编辑用户</button><button class="sm danger" type="button" data-action="delete-user" data-id="${script.id}" data-name="${esc(user.name)}">删除用户</button></div>
    </div>
    <div class="qk-row">任务前脚本：${user.preRunScript ? `<span class="mono">${esc(user.preRunScript)}</span>${user.preRunOnceOnly ? "（仅首次）" : ""}` : '<span class="muted">未设置</span>'}</div>
    <div class="qk-row">任务后脚本：${user.postRunScript ? `<span class="mono">${esc(user.postRunScript)}</span>${user.postRunOnFinalOnly ? "（仅最终完成）" : ""}` : '<span class="muted">未设置</span>'}</div>
  </article>`).join("") : '<div class="empty"><strong>暂无用户</strong>点击右上角「添加用户」创建。</div>';
  render(pageHeader("SCRIPT USERS", `${esc(script.name)} · 用户管理`, "为不同用户保存独立配置，运行时会自动交换并还原。", `<button type="button" data-action="open-user-modal" data-id="${script.id}">添加用户</button>`) + `<div class="back-row"><a class="back-link" href="#/scripts">← 返回脚本实例</a></div>${usersMarkup}`);
}

export function openUserModal(scriptId, userName = "") {
  const script = state.scripts.find(item => item.id === scriptId);
  if (!script) { toast("脚本不存在", "error"); return; }
  const user = userName ? (script.users || []).find(item => item.name === userName) : null;
  state.userModalScriptId = scriptId;
  state.userEditingName = userName || null;
  state.userDraft = {
    name: user?.name || "", enabled: user?.enabled !== false, preRunScript: user?.preRunScript || "", preRunOnceOnly: !!user?.preRunOnceOnly,
    postRunScript: user?.postRunScript || "", postRunOnFinalOnly: !!user?.postRunOnFinalOnly,
  };
  const d = state.userDraft;
  const body = `${valueField("um-name", "用户名 <span class='req'>*</span>", d.name, "text", 'placeholder="脚本内不可重复"')}
    <label class="check"><input id="um-enabled" type="checkbox" ${d.enabled ? "checked" : ""}><span>启用该用户（禁用后不可选用于运行）</span></label>
    <div class="subsection"><h3>任务前运行脚本</h3>${valueField("um-pre", "脚本路径（填写地址则启用，留空不启用）", d.preRunScript)}<label class="check"><input id="um-pre-once" type="checkbox" ${d.preRunOnceOnly ? "checked" : ""}><span>仅首次运行启用（重试时不再执行）</span></label></div>
    <div class="subsection"><h3>任务后运行脚本</h3>${valueField("um-post", "脚本路径（填写地址则启用，留空不启用）", d.postRunScript)}<label class="check"><input id="um-post-final" type="checkbox" ${d.postRunOnFinalOnly ? "checked" : ""}><span>仅最终运行完成启用</span></label></div>`;
  showModal(modalShell(user ? "编辑用户" : "添加用户", body, '<button type="button" data-action="save-user">保存</button><button class="ghost" type="button" data-action="close-modal">取消</button>'));
}

export async function saveUser() {
  const name = $("#um-name")?.value.trim();
  if (!name) { toast("请填写用户名", "error"); $("#um-name")?.focus(); return; }
  const payload = { name, enabled: $("#um-enabled").checked, preRunScript: $("#um-pre").value.trim(), preRunOnceOnly: $("#um-pre-once").checked, postRunScript: $("#um-post").value.trim(), postRunOnFinalOnly: $("#um-post-final").checked };
  try {
    const base = "/api/scripts/" + state.userModalScriptId + "/users";
    if (state.userEditingName) await api("PUT", base + "/" + encodeURIComponent(state.userEditingName), payload);
    else await api("POST", base, payload);
    const id = state.userModalScriptId;
    closeModal(); toast("用户已保存");
    await pageScriptUsers(id, state.routeToken);
  } catch (error) { toast(error.message, "error"); }
}

export async function deleteUser(scriptId, userName) {
  if (!confirm("确定删除用户「" + userName + "」？（该用户保存的配置会一并删除）")) return;
  try { await api("DELETE", `/api/scripts/${scriptId}/users/${encodeURIComponent(userName)}`); toast("用户已删除"); await pageScriptUsers(scriptId, state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export async function editUserConfig(scriptId, userName) {
  try {
    await api("POST", `/api/scripts/${scriptId}/users/${encodeURIComponent(userName)}/edit-config`, { action: "start" });
    showModal(modalShell("配置编辑中", `<p class="modal-copy">主程序已启动（不带参数）。请自行设置好当前用户「${esc(userName)}」的脚本配置。设置完成后点击「完成」保存，或点击「取消」放弃本次修改。</p>`, `<button type="button" data-action="edit-config-done" data-id="${scriptId}" data-name="${esc(userName)}">完成</button><button class="ghost" type="button" data-action="edit-config-cancel" data-id="${scriptId}" data-name="${esc(userName)}">取消</button>`));
  } catch (error) { toast(error.message, "error"); }
}

export async function editConfigAction(scriptId, userName, action) {
  try { await api("POST", `/api/scripts/${scriptId}/users/${encodeURIComponent(userName)}/edit-config`, { action }); closeModal(); toast(action === "done" ? "已保存当前用户配置" : "已取消，配置已还原"); await pageScriptUsers(scriptId, state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export async function pageQueues(token) {
  if (!isCurrent("queues", token)) return;
  navActive("queues"); setTopbarTitle("调度队列");
  let queues, scripts;
  try { [queues, scripts] = await Promise.all([api("GET", "/api/queues"), api("GET", "/api/scripts")]); }
  catch (error) { render(`<div class="empty"><strong>加载队列失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("queues", token)) return;
  state.queues = queues; state.scripts = scripts;
  const content = queues.length ? queues.map(queue => `<article class="card queue-card">
    <div class="list-item-head"><div><div class="list-item-title"><strong>${esc(queue.name)}</strong><span class="badge blue">${queue.autoRunMode === "startup" ? "启动时运行" : "定时运行"}</span><span class="badge muted">完成操作：${esc(actionLabel(queue.completionAction))}</span>${queue.notifyEnabled ? '<span class="badge ok">队列级通知</span>' : ""}</div></div>
      <div class="action-row"><button class="sm" type="button" data-action="edit-queue" data-id="${queue.id}">编辑</button><button class="sm danger" type="button" data-action="delete-queue" data-id="${queue.id}" data-name="${esc(queue.name)}">删除</button></div>
    </div>
    <div class="qk-row">定时：${(queue.timeSets || []).filter(item => item.enabled).map(item => dayDesc(item.days) + " " + item.time).join("；") || '<span class="badge bad">无</span>'}</div>
    <div class="qk-row">任务：${(queue.tasks || []).slice().sort((a, b) => a.index - b.index).map(item => { const script = scripts.find(value => value.id === item.scriptInstanceId); return esc(script ? script.name : "(缺失)"); }).join(" → ") || "无"}</div>
  </article>`).join("") : '<div class="empty"><strong>暂无调度队列</strong>点击右上角「新建调度队列」创建。</div>';
  render(pageHeader("SCHEDULER", "调度队列", "把多个脚本串成可重复执行的工作流。", '<button type="button" data-action="open-queue-modal">新建调度队列</button>') + content);
}

export async function openQueueModal(id = "") {
  let queue = id ? state.queues.find(item => item.id === id) : null;
  if (id && !queue) {
    try { state.queues = await api("GET", "/api/queues"); queue = state.queues.find(item => item.id === id); }
    catch (error) { toast("加载队列失败：" + error.message, "error"); return; }
  }
  const value = queue || {};
  state.queueDraft = {
    id: value.id || "", name: value.name || "", autoRunMode: value.autoRunMode || "scheduled", completionAction: value.completionAction || "none", notifyEnabled: !!value.notifyEnabled,
    timeSets: (value.timeSets || []).map(item => ({ id: item.id, enabled: !!item.enabled, days: [...(item.days || [])], time: item.time })),
    tasks: (value.tasks || []).map(item => ({ id: item.id, index: item.index, scriptInstanceId: item.scriptInstanceId })),
  };
  if (!state.queueDraft.timeSets.length) state.queueDraft.timeSets.push({ id: "", enabled: true, days: [1, 2, 3, 4, 5], time: "05:30" });
  renderQueueModal();
}

function syncQueueDraftFromDom() {
  if (!state.queueDraft) return;
  const name = $("#qm-name"); if (name) state.queueDraft.name = name.value.trim();
  const mode = $("#qm-mode"); if (mode) state.queueDraft.autoRunMode = mode.value;
  const action = $("#qm-action"); if (action) state.queueDraft.completionAction = action.value;
  const notify = $("#qm-notify"); if (notify) state.queueDraft.notifyEnabled = notify.checked;
  $$(".timeset-card").forEach((card, index) => {
    const target = state.queueDraft.timeSets[index]; if (!target) return;
    const enabled = card.querySelector("[data-ts-enable]"); if (enabled) target.enabled = enabled.checked;
    const time = card.querySelector("[data-ts-time]"); if (time) target.time = time.value.trim() || target.time;
    const dayInputs = Array.from(card.querySelectorAll("[data-ts-days]")).filter(input => input.checked);
    target.days = dayInputs.map(input => +input.dataset.day);
  });
  $$('[data-task-idx]').forEach(select => { const index = +select.dataset.taskIdx; if (state.queueDraft.tasks[index]) state.queueDraft.tasks[index].scriptInstanceId = select.value; });
}

export function renderQueueModal() {
  syncQueueDraftFromDom();
  const d = state.queueDraft;
  const scripts = state.scripts;
  const days = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
  const body = `${valueField("qm-name", "队列名称 <span class='req'>*</span>", d.name)}
    <div class="form-grid"><div><label class="field-label" for="qm-mode">自动运行方式</label><select id="qm-mode"><option value="scheduled" ${d.autoRunMode === "scheduled" ? "selected" : ""}>定时运行</option><option value="startup" ${d.autoRunMode === "startup" ? "selected" : ""}>启动时运行</option></select></div><div><label class="field-label" for="qm-action">运行完成操作</label><select id="qm-action"><option value="none" ${d.completionAction === "none" ? "selected" : ""}>无操作</option><option value="exit" ${d.completionAction === "exit" ? "selected" : ""}>退出软件</option><option value="sleep" ${d.completionAction === "sleep" ? "selected" : ""}>休眠</option><option value="reboot" ${d.completionAction === "reboot" ? "selected" : ""}>重启</option><option value="shutdown" ${d.completionAction === "shutdown" ? "selected" : ""}>关机</option></select></div></div>
    <label class="check"><input id="qm-notify" type="checkbox" ${d.notifyEnabled ? "checked" : ""}><span>队列级通知（统一发送所有脚本状态，覆盖实例级设置）</span></label>
    <div class="subsection"><div class="section-heading"><h3>定时列表</h3><span class="muted">可添加多个触发时间</span></div><div id="qm-timesets">${d.timeSets.map((timeSet, index) => `<div class="card timeset-card compact-card"><div class="timeset-layout"><div class="timeset-days"><label class="field-label">执行周期（可多选）</label><div class="days-frame" role="group" aria-label="执行周期">${days.map((name, day) => `<label class="check days-option"><input type="checkbox" data-ts-days="${index}" data-day="${day}" ${timeSet.days.includes(day) ? "checked" : ""}><span>${name}</span></label>`).join("")}</div></div><div class="timeset-time"><label class="field-label" for="ts-time-${index}">执行时间</label><input id="ts-time-${index}" type="time" data-ts-time="${index}" value="${esc(timeSet.time)}"></div></div><div class="timeset-actions"><label class="check"><input type="checkbox" data-ts-enable="${index}" ${timeSet.enabled ? "checked" : ""}><span>启用</span></label><button class="sm danger" type="button" data-action="remove-time-set" data-index="${index}">删除该定时</button></div></div>`).join("")}</div><button class="ghost" type="button" data-action="add-time-set">+ 添加定时</button></div>
    <div class="subsection"><div class="section-heading"><h3>任务列表</h3><span class="muted">按序号先后执行</span></div><div id="qm-tasks">${d.tasks.slice().sort((a, b) => a.index - b.index).map((task, index) => `<div class="list-item task-row"><span class="muted task-number">${index + 1}.</span><select data-task-idx="${index}"><option value="">（选择脚本实例）</option>${scripts.map(script => `<option value="${script.id}" ${script.id === task.scriptInstanceId ? "selected" : ""}>${esc(script.name)}</option>`).join("")}</select><button class="sm" type="button" data-action="move-task-up" data-index="${index}" aria-label="任务上移">↑</button><button class="sm" type="button" data-action="move-task-down" data-index="${index}" aria-label="任务下移">↓</button><button class="sm danger" type="button" data-action="remove-task" data-index="${index}">删除</button></div>`).join("")}</div><button class="ghost" type="button" data-action="add-task">+ 添加任务</button></div>`;
  showModal(modalShell(d.id ? "编辑调度队列" : "新建调度队列", body, '<button type="button" data-action="save-queue">保存</button><button class="ghost" type="button" data-action="close-modal">取消</button>'), true);
}

export function queueAddTimeSet() { state.queueDraft.timeSets.push({ id: "", enabled: true, days: [1, 2, 3, 4, 5], time: "05:30" }); renderQueueModal(); }
export function queueRemoveTimeSet(index) { syncQueueDraftFromDom(); state.queueDraft.timeSets.splice(index, 1); renderQueueModal(); }
export function queueAddTask() { syncQueueDraftFromDom(); state.queueDraft.tasks.push({ id: "", index: state.queueDraft.tasks.length, scriptInstanceId: "" }); renderQueueModal(); }
export function queueRemoveTask(index) { syncQueueDraftFromDom(); state.queueDraft.tasks.splice(index, 1); state.queueDraft.tasks.forEach((task, i) => task.index = i); renderQueueModal(); }
export function queueMoveTask(index, direction) { syncQueueDraftFromDom(); const other = index + direction; if (other < 0 || other >= state.queueDraft.tasks.length) return; [state.queueDraft.tasks[index], state.queueDraft.tasks[other]] = [state.queueDraft.tasks[other], state.queueDraft.tasks[index]]; state.queueDraft.tasks.forEach((task, i) => task.index = i); renderQueueModal(); }

export async function saveQueue() {
  syncQueueDraftFromDom();
  const draft = state.queueDraft;
  draft.timeSets.forEach((timeSet, index) => { timeSet.enabled = $(`[data-ts-enable='${index}']`)?.checked ?? timeSet.enabled; timeSet.time = $(`[data-ts-time='${index}']`)?.value.trim() || "05:30"; timeSet.days = $$(`[data-ts-days='${index}']`).filter(input => input.checked).map(input => +input.dataset.day); });
  draft.timeSets = draft.timeSets.filter(timeSet => timeSet.days.length);
  draft.tasks = draft.tasks.map((task, index) => ({ ...task, index, scriptInstanceId: $(`[data-task-idx='${index}']`)?.value || task.scriptInstanceId })).filter(task => task.scriptInstanceId);
  if (!draft.name) { toast("队列名称不能为空", "error"); return; }
  if (!draft.tasks.length) { toast("任务列表为空，请至少添加一个脚本任务", "error"); return; }
  try { if (draft.id) await api("PUT", "/api/queues/" + draft.id, draft); else await api("POST", "/api/queues", draft); closeModal(); toast("调度队列已保存"); await pageQueues(state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export async function deleteQueue(id, name) {
  if (!confirm("确定删除调度队列「" + name + "」？")) return;
  try { await api("DELETE", "/api/queues/" + id); toast("调度队列已删除"); await pageQueues(state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}
