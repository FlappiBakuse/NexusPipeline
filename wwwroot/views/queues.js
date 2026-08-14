import { api } from "../core/api.js";
import { $, $$ } from "../core/dom.js";
import { esc, scriptFallbackIcon } from "../core/format.js";
import { pageHeader, valueField } from "../core/forms.js";
import { pagerMarkup, registerPager } from "../core/pager.js";
import { isCurrent, notifyAvailable, registerInterval, state } from "../core/state.js";
import { closeModal, confirmModal, modalShell, showModal } from "../core/modal.js";
import { navActive, render, setTopbarTitle, toast } from "../core/ui.js";

let queueDraft = null;
let queuePage = 1;
let nextTimer = null;
let queuePendingMerged = false;
const QUEUE_PAGE_SIZE = 20;
const FALLBACK_ICON = scriptFallbackIcon;

export async function pageQueues(token) {
  if (!isCurrent("queues", token)) return;
  navActive("queues"); setTopbarTitle("调度队列");
  let queues, scripts, status;
  try { [queues, scripts, status] = await Promise.all([api("GET", "/api/queues"), api("GET", "/api/scripts"), api("GET", "/api/status")]); }
  catch (error) { render(`<div class="empty"><strong>加载队列失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("queues", token)) return;
  state.queues = queues; state.scripts = scripts; state.plugins = status.plugins || [];
  const atLimit = !!(state.limits && queues.length >= state.limits.maxQueues);
  const action = `<button type="button" data-action="open-queue-modal" ${atLimit ? "disabled" : ""}>新建调度队列${atLimit ? `（${queues.length}/${state.limits.maxQueues}）` : ""}</button>`;
  const totalPages = Math.max(1, Math.ceil(queues.length / QUEUE_PAGE_SIZE));
  if (queuePage > totalPages) queuePage = totalPages;
  const pageItems = queues.slice((queuePage - 1) * QUEUE_PAGE_SIZE, queuePage * QUEUE_PAGE_SIZE);
  const content = queues.length ? `<section class="card"><div class="script-grid">
    ${pageItems.map(queue => queueCardMarkup(queue, scripts)).join("")}
    </div>${pagerMarkup("queues", queuePage, QUEUE_PAGE_SIZE, queues.length)}</section>` : '<div class="empty"><strong>暂无调度队列</strong>点击右上角「新建调度队列」创建。</div>';
  render(pageHeader("SCHEDULER", "调度队列", "把多个脚本串成可重复执行的工作流。", action) + content);
  registerPager("queues", p => { queuePage = p; pageQueues(state.routeToken); });
  tickQueueNext();
  if (nextTimer) clearInterval(nextTimer);
  nextTimer = setInterval(tickQueueNext, 1000);
  registerInterval(nextTimer);
  $domIcons();
}

function queueCardMarkup(queue, scripts) {
  const firstTask = (queue.tasks || []).slice().sort((a, b) => a.index - b.index).find(item => scripts.some(script => script.id === item.scriptInstanceId));
  const firstScript = firstTask ? scripts.find(script => script.id === firstTask.scriptInstanceId) : null;
  const nextAt = queue.nextTrigger ? new Date(queue.nextTrigger).getTime() : 0;
  const timeBadge = queue.autoRunMode === "scheduled"
    ? `<span class="badge blue queue-next" data-next="${nextAt || ""}">${nextAt ? "正在计算倒计时" : "等待定时触发"}</span>`
    : queue.autoRunMode === "startup"
      ? '<span class="badge blue">将在下次启动开始运行</span>'
      : "";
  const notifyBadge = notifyAvailable()
    ? `<span class="badge ${queue.notifyEnabled ? "ok" : "muted"}" data-testid="queue-notify">队列级通知：${queue.notifyEnabled ? "开" : "关"}</span>`
    : "";
  const badgesRow = timeBadge || notifyBadge ? `<div class="script-name-row">${timeBadge}${notifyBadge}</div>` : "";
  return `<article class="script-card queue-card" data-testid="queue-card">
    <img class="script-ico" src="/api/scripts/${firstScript ? firstScript.id : "none"}/icon" alt="" loading="lazy" data-fallback="${esc(FALLBACK_ICON)}">
    <div class="script-main">
      <div class="script-name-row"><strong class="scroll-text"><span class="scroll-inner">${esc(queue.name)}</span></strong></div>
      ${badgesRow}
    </div>
    <div class="queue-ops">
      <button class="sm" type="button" data-action="edit-queue" data-id="${queue.id}">编辑队列</button>
      <button class="sm danger" type="button" data-action="delete-queue" data-id="${queue.id}" data-name="${esc(queue.name)}">删除队列</button>
    </div>
  </article>`;
}

function tickQueueNext() {
  const now = Date.now();
  $$("#view .queue-next[data-next]").forEach(el => {
    const target = +(el.dataset.next || 0);
    if (!target) return;
    const remain = target - now;
    if (remain <= 0) {
      el.textContent = "即将开始运行";
      return;
    }
    const seconds = Math.floor(remain / 1000);
    const hours = String(Math.floor(seconds / 3600)).padStart(2, "0");
    const minutes = String(Math.floor(seconds % 3600 / 60)).padStart(2, "0");
    const secs = String(seconds % 60).padStart(2, "0");
    el.textContent = `${hours}:${minutes}:${secs}后开始`;
  });
}

function $domIcons() {
  $$("#view .script-ico").forEach(img => {
    img.addEventListener("error", () => {
      if (img.dataset.fallback && !img.src.startsWith("data:")) img.src = img.dataset.fallback;
    }, { once: true });
  });
}

export async function openQueueModal(id = "") {
  let queue = id ? state.queues.find(item => item.id === id) : null;
  if (id && !queue) {
    try { state.queues = await api("GET", "/api/queues"); queue = state.queues.find(item => item.id === id); }
    catch (error) { toast("加载队列失败：" + error.message, "error"); return; }
  }
  const value = queue || {};
  queueDraft = {
    id: value.id || "", name: value.name || "", autoRunMode: value.autoRunMode || "none", completionAction: value.completionAction || "none", notifyEnabled: !!value.notifyEnabled,
    timeSets: (value.timeSets || []).map(item => ({ id: item.id, enabled: !!item.enabled, days: [...(item.days || [])], time: item.time })),
    tasks: (value.tasks || []).map(item => ({ id: item.id, index: item.index, scriptInstanceId: item.scriptInstanceId })),
  };
  if (!queueDraft.timeSets.length) queueDraft.timeSets.push({ id: "", enabled: true, days: [1, 2, 3, 4, 5], time: "05:30" });
  renderQueueModal();
}

function syncQueueDraftFromDom() {
  if (!queueDraft) return;
  const name = $("#qm-name"); if (name) queueDraft.name = name.value.trim();
  const mode = $("#qm-mode"); if (mode) queueDraft.autoRunMode = mode.value;
  const action = $("#qm-action"); if (action) queueDraft.completionAction = action.value;
  const notify = $("#qm-notify"); if (notify) queueDraft.notifyEnabled = notify.getAttribute("aria-pressed") === "true";
  $$(".timeset-card").forEach((card, index) => {
    const target = queueDraft.timeSets[index]; if (!target) return;
    const enabled = card.querySelector("[data-ts-enable]"); if (enabled) target.enabled = enabled.getAttribute("aria-pressed") === "true";
    const time = card.querySelector("[data-ts-time]"); if (time) target.time = time.value.trim() || target.time;
    const dayButtons = Array.from(card.querySelectorAll("[data-ts-days]")).filter(input => input.getAttribute("aria-pressed") === "true");
    target.days = dayButtons.map(input => +input.dataset.day);
  });
  $$('[data-task-idx]').forEach(select => { const index = +select.dataset.taskIdx; if (queueDraft.tasks[index]) queueDraft.tasks[index].scriptInstanceId = select.value; });
}

export function renderQueueModal() {
  syncQueueDraftFromDom();
  const d = queueDraft;
  const scripts = state.scripts;
  const days = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
  const l = state.limits || {};
  const timeSetAtLimit = !!(l.maxTimeSetsPerQueue && d.timeSets.length >= l.maxTimeSetsPerQueue);
  const body = `${valueField("qm-name", "队列名称 <span class='req'>*</span>", d.name)}
    <div class="form-grid"><div><label class="field-label" for="qm-mode">自动运行方式</label><select id="qm-mode"><option value="none" ${d.autoRunMode === "none" ? "selected" : ""}>不运行</option><option value="scheduled" ${d.autoRunMode === "scheduled" ? "selected" : ""}>定时运行</option><option value="startup" ${d.autoRunMode === "startup" ? "selected" : ""}>启动时运行</option></select></div><div><label class="field-label" for="qm-action">运行完成操作</label><select id="qm-action"><option value="none" ${d.completionAction === "none" ? "selected" : ""}>无操作</option><option value="exit" ${d.completionAction === "exit" ? "selected" : ""}>退出软件</option><option value="sleep" ${d.completionAction === "sleep" ? "selected" : ""}>休眠</option><option value="reboot" ${d.completionAction === "reboot" ? "selected" : ""}>重启</option><option value="shutdown" ${d.completionAction === "shutdown" ? "selected" : ""}>关机</option></select></div></div>
    <div class="toggle-row"><button class="mode-toggle" type="button" data-action="toggle-qm-flag" id="qm-notify" ${notifyAvailable() ? "" : "hidden"} aria-pressed="${d.notifyEnabled ? "true" : "false"}">队列通知</button><span class="muted">统一发送所有脚本状态，覆盖实例级设置</span></div>
    <div class="subsection"><div class="section-heading"><h3>定时列表</h3><span class="muted">可添加多个触发时间</span></div><div id="qm-timesets">${d.timeSets.map((timeSet, index) => `<div class="card timeset-card compact-card"><div class="timeset-layout"><div class="timeset-days"><label class="field-label">执行周期（可多选）</label><div class="days-btn-grid" role="group" aria-label="执行周期">${days.map((name, day) => `<button class="mode-toggle" type="button" data-action="toggle-ts-day" data-ts-days="${index}" data-day="${day}" aria-pressed="${timeSet.days.includes(day) ? "true" : "false"}" title="${esc(name)}" aria-label="${esc(name)}">${esc("日一二三四五六"[day])}</button>`).join("")}</div></div><div class="timeset-time"><label class="field-label" for="ts-time-${index}">执行时间</label><input id="ts-time-${index}" type="time" data-ts-time="${index}" value="${esc(timeSet.time)}"></div></div><div class="timeset-actions"><button class="mode-toggle" type="button" data-action="toggle-ts-enable" data-ts-enable="${index}" aria-pressed="${timeSet.enabled ? "true" : "false"}">启用</button><button class="sm danger" type="button" data-action="remove-time-set" data-index="${index}">删除</button></div></div>`).join("")}</div><button class="ghost" type="button" data-action="add-time-set" ${timeSetAtLimit ? "disabled" : ""}>+ 添加定时${timeSetAtLimit ? `（${d.timeSets.length}/${l.maxTimeSetsPerQueue}）` : ""}</button></div>
    <div class="subsection"><div class="section-heading"><h3>任务列表</h3><span class="muted">按序号先后执行</span></div><div id="qm-tasks">${d.tasks.slice().sort((a, b) => a.index - b.index).map((task, index) => `<div class="list-item task-row"><span class="muted task-number">${index + 1}.</span><select data-task-idx="${index}"><option value="">（选择脚本实例）</option>${scripts.map(script => `<option value="${esc(script.id)}" ${script.id === task.scriptInstanceId ? "selected" : ""}>${esc(script.name)}</option>`).join("")}</select><button class="sm" type="button" data-action="move-task-up" data-index="${index}" aria-label="任务上移">↑</button><button class="sm" type="button" data-action="move-task-down" data-index="${index}" aria-label="任务下移">↓</button><button class="sm danger" type="button" data-action="remove-task" data-index="${index}">删除</button></div>`).join("")}</div><button class="ghost" type="button" data-action="add-task">+ 添加任务</button></div>`;
  showModal(modalShell(d.id ? "编辑调度队列" : "新建调度队列", body, '<button type="button" data-action="save-queue">保存</button><button class="ghost" type="button" data-action="close-modal">取消</button>'), true);
}

function queueTotalUsers() {
  if (!queueDraft) return 0;
  return queueDraft.tasks.reduce((sum, task) => {
    const script = state.scripts.find(item => item.id === task.scriptInstanceId);
    if (!script) return sum + 1;
    return sum + Math.max(1, (script.users || []).filter(user => user.enabled).length);
  }, 0);
}

export function queueAddTimeSet() { queueDraft.timeSets.push({ id: "", enabled: true, days: [1, 2, 3, 4, 5], time: "05:30" }); renderQueueModal(); }
export function queueRemoveTimeSet(index) { syncQueueDraftFromDom(); queueDraft.timeSets.splice(index, 1); renderQueueModal(); }
export function queueAddTask() {
  syncQueueDraftFromDom();
  const l = state.limits || {};
  const current = queueTotalUsers();
  if (l.maxQueueTotalUsers && current + 1 > l.maxQueueTotalUsers) {
    toast(`任务列表的启用用户总数已达上限（${current}/${l.maxQueueTotalUsers}）`, "error");
    return;
  }
  queueDraft.tasks.push({ id: "", index: queueDraft.tasks.length, scriptInstanceId: "" }); renderQueueModal();
}
export function queueRemoveTask(index) { syncQueueDraftFromDom(); queueDraft.tasks.splice(index, 1); queueDraft.tasks.forEach((task, i) => task.index = i); renderQueueModal(); }
export function queueMoveTask(index, direction) { syncQueueDraftFromDom(); const other = index + direction; if (other < 0 || other >= queueDraft.tasks.length) return; [queueDraft.tasks[index], queueDraft.tasks[other]] = [queueDraft.tasks[other], queueDraft.tasks[index]]; queueDraft.tasks.forEach((task, i) => task.index = i); renderQueueModal(); }

export async function saveQueue() {
  syncQueueDraftFromDom();
  const draft = queueDraft;
  draft.timeSets.forEach((timeSet, index) => { const tsEnable = $(`[data-ts-enable='${index}']`); if (tsEnable) timeSet.enabled = tsEnable.getAttribute("aria-pressed") === "true"; timeSet.time = $(`[data-ts-time='${index}']`)?.value.trim() || "05:30"; timeSet.days = $$(`[data-ts-days='${index}']`).filter(input => input.getAttribute("aria-pressed") === "true").map(input => +input.dataset.day); });
  draft.timeSets = draft.timeSets.filter(timeSet => timeSet.days.length);
  draft.tasks = draft.tasks.map((task, index) => ({ ...task, index, scriptInstanceId: $(`[data-task-idx='${index}']`)?.value || task.scriptInstanceId })).filter(task => task.scriptInstanceId);
  if (!draft.name) { toast("队列名称不能为空", "error"); return; }
  if (!draft.tasks.length) { toast("任务列表为空，请至少添加一个脚本任务", "error"); return; }
  const l = state.limits || {};
  const nameBytes = new TextEncoder().encode(draft.name).length;
  if (l.maxQueueNameBytes && nameBytes > l.maxQueueNameBytes) { toast(`队列名称最多 ${l.maxQueueNameBytes} 字节`, "error"); return; }
  if (l.maxTimeSetsPerQueue && draft.timeSets.length > l.maxTimeSetsPerQueue) { toast(`定时列表已达上限（${draft.timeSets.length}/${l.maxTimeSetsPerQueue}）`, "error"); return; }
  const totalUsers = queueTotalUsers();
  if (l.maxQueueTotalUsers && totalUsers > l.maxQueueTotalUsers) { toast(`任务列表的启用用户总数已达上限（${totalUsers}/${l.maxQueueTotalUsers}）`, "error"); return; }
  const mergedCount = mergeTimeSets();
  queuePendingMerged = mergedCount > 0;
  if (hasTimeGap()) {
    showModal(modalShell("定时间隔警告", `<p class="modal-copy">存在间隔低于10分钟的定时任务，如果之前的定时任务还未完成，之后的定时任务可能会忽略，确定吗？</p>`, '<button type="button" data-action="confirm-timegap-save">确定</button><button class="ghost" type="button" data-action="cancel-timegap">取消</button>'));
    return;
  }
  await doSaveQueue(queuePendingMerged);
}

/** 定时列表去重合并（启用+执行周期+时间完全一致视为同一条），修改 draft 并返回被合并条数。</summary> */
function mergeTimeSets() {
  const seen = new Set();
  const merged = [];
  let removed = 0;
  for (const timeSet of queueDraft.timeSets) {
    const key = `${timeSet.enabled}|${[...timeSet.days].sort((a, b) => a - b).join(",")}|${timeSet.time}`;
    if (seen.has(key)) { removed++; continue; }
    seen.add(key);
    merged.push(timeSet);
  }
  queueDraft.timeSets = merged;
  return removed;
}

/** 是否存在间隔低于 10 分钟的定时触发点：启用列表按一周内分钟数排序，相邻差或跨周首尾差 &lt;10 即命中。</summary> */
function hasTimeGap() {
  const minutes = [];
  for (const timeSet of queueDraft.timeSets) {
    if (!timeSet.enabled) continue;
    const parts = String(timeSet.time).split(":");
    const hm = (+parts[0] || 0) * 60 + (+parts[1] || 0);
    for (const day of timeSet.days) minutes.push(day * 1440 + hm);
  }
  if (minutes.length < 2) return false;
  const sorted = [...new Set(minutes)].sort((a, b) => a - b);
  for (let i = 1; i < sorted.length; i++) {
    if (sorted[i] - sorted[i - 1] < 10) return true;
  }
  return sorted[0] + 7 * 1440 - sorted[sorted.length - 1] < 10;
}

function cancelTimeGap() {
  closeModal();
  renderQueueModal();
}

async function doSaveQueue(merged) {
  const draft = queueDraft;
  try {
    if (draft.id) await api("PUT", "/api/queues/" + draft.id, draft);
    else await api("POST", "/api/queues", draft);
    closeModal();
    toast(merged ? "完全一致的定时列表已被合并" : "调度队列已保存");
    await pageQueues(state.routeToken);
  } catch (error) { toast(error.message, "error"); }
}

export function deleteQueue(id, name) {
  confirmModal("删除调度队列", `确定删除调度队列「${esc(name)}」？`, "confirm-delete-queue", { id, name });
}

export async function confirmDeleteQueue(id, name) {
  try { await api("DELETE", "/api/queues/" + id); closeModal(); toast("调度队列已删除"); await pageQueues(state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "open-queue-modal": target => openQueueModal(target.dataset.id || ""),
  "edit-queue": target => openQueueModal(target.dataset.id),
  "delete-queue": target => deleteQueue(target.dataset.id, target.dataset.name),
  "confirm-delete-queue": target => confirmDeleteQueue(target.dataset.id, target.dataset.name),
  "save-queue": () => saveQueue(),
  "confirm-timegap-save": () => doSaveQueue(queuePendingMerged),
  "cancel-timegap": () => cancelTimeGap(),
  "add-time-set": () => queueAddTimeSet(),
  "remove-time-set": target => queueRemoveTimeSet(+target.dataset.index),
  "add-task": () => queueAddTask(),
  "remove-task": target => queueRemoveTask(+target.dataset.index),
  "move-task-up": target => queueMoveTask(+target.dataset.index, -1),
  "move-task-down": target => queueMoveTask(+target.dataset.index, 1),
  "toggle-qm-flag": () => { const btn = $("#qm-notify"); if (btn) togglePressed(btn); },
  "toggle-ts-day": target => togglePressed(target),
  "toggle-ts-enable": target => togglePressed(target),
};

function togglePressed(btn) {
  btn.setAttribute("aria-pressed", btn.getAttribute("aria-pressed") === "true" ? "false" : "true");
}
