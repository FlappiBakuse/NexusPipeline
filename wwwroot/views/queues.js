import { api } from "../core/api.js";
import { $, $$ } from "../core/dom.js";
import { actionLabel, dayDesc, esc } from "../core/format.js";
import { pageHeader, valueField } from "../core/forms.js";
import { isCurrent, state } from "../core/state.js";
import { closeModal, modalShell, showModal } from "../core/modal.js";
import { navActive, render, setTopbarTitle, toast } from "../core/ui.js";

let queueDraft = null;

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
  queueDraft = {
    id: value.id || "", name: value.name || "", autoRunMode: value.autoRunMode || "scheduled", completionAction: value.completionAction || "none", notifyEnabled: !!value.notifyEnabled,
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
  const notify = $("#qm-notify"); if (notify) queueDraft.notifyEnabled = notify.checked;
  $$(".timeset-card").forEach((card, index) => {
    const target = queueDraft.timeSets[index]; if (!target) return;
    const enabled = card.querySelector("[data-ts-enable]"); if (enabled) target.enabled = enabled.checked;
    const time = card.querySelector("[data-ts-time]"); if (time) target.time = time.value.trim() || target.time;
    const dayInputs = Array.from(card.querySelectorAll("[data-ts-days]")).filter(input => input.checked);
    target.days = dayInputs.map(input => +input.dataset.day);
  });
  $$('[data-task-idx]').forEach(select => { const index = +select.dataset.taskIdx; if (queueDraft.tasks[index]) queueDraft.tasks[index].scriptInstanceId = select.value; });
}

export function renderQueueModal() {
  syncQueueDraftFromDom();
  const d = queueDraft;
  const scripts = state.scripts;
  const days = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
  const body = `${valueField("qm-name", "队列名称 <span class='req'>*</span>", d.name)}
    <div class="form-grid"><div><label class="field-label" for="qm-mode">自动运行方式</label><select id="qm-mode"><option value="scheduled" ${d.autoRunMode === "scheduled" ? "selected" : ""}>定时运行</option><option value="startup" ${d.autoRunMode === "startup" ? "selected" : ""}>启动时运行</option></select></div><div><label class="field-label" for="qm-action">运行完成操作</label><select id="qm-action"><option value="none" ${d.completionAction === "none" ? "selected" : ""}>无操作</option><option value="exit" ${d.completionAction === "exit" ? "selected" : ""}>退出软件</option><option value="sleep" ${d.completionAction === "sleep" ? "selected" : ""}>休眠</option><option value="reboot" ${d.completionAction === "reboot" ? "selected" : ""}>重启</option><option value="shutdown" ${d.completionAction === "shutdown" ? "selected" : ""}>关机</option></select></div></div>
    <label class="check"><input id="qm-notify" type="checkbox" ${d.notifyEnabled ? "checked" : ""}><span>队列级通知（统一发送所有脚本状态，覆盖实例级设置）</span></label>
    <div class="subsection"><div class="section-heading"><h3>定时列表</h3><span class="muted">可添加多个触发时间</span></div><div id="qm-timesets">${d.timeSets.map((timeSet, index) => `<div class="card timeset-card compact-card"><div class="timeset-layout"><div class="timeset-days"><label class="field-label">执行周期（可多选）</label><div class="days-frame" role="group" aria-label="执行周期">${days.map((name, day) => `<label class="check days-option"><input type="checkbox" data-ts-days="${index}" data-day="${day}" ${timeSet.days.includes(day) ? "checked" : ""}><span>${name}</span></label>`).join("")}</div></div><div class="timeset-time"><label class="field-label" for="ts-time-${index}">执行时间</label><input id="ts-time-${index}" type="time" data-ts-time="${index}" value="${esc(timeSet.time)}"></div></div><div class="timeset-actions"><label class="check"><input type="checkbox" data-ts-enable="${index}" ${timeSet.enabled ? "checked" : ""}><span>启用</span></label><button class="sm danger" type="button" data-action="remove-time-set" data-index="${index}">删除该定时</button></div></div>`).join("")}</div><button class="ghost" type="button" data-action="add-time-set">+ 添加定时</button></div>
    <div class="subsection"><div class="section-heading"><h3>任务列表</h3><span class="muted">按序号先后执行</span></div><div id="qm-tasks">${d.tasks.slice().sort((a, b) => a.index - b.index).map((task, index) => `<div class="list-item task-row"><span class="muted task-number">${index + 1}.</span><select data-task-idx="${index}"><option value="">（选择脚本实例）</option>${scripts.map(script => `<option value="${script.id}" ${script.id === task.scriptInstanceId ? "selected" : ""}>${esc(script.name)}</option>`).join("")}</select><button class="sm" type="button" data-action="move-task-up" data-index="${index}" aria-label="任务上移">↑</button><button class="sm" type="button" data-action="move-task-down" data-index="${index}" aria-label="任务下移">↓</button><button class="sm danger" type="button" data-action="remove-task" data-index="${index}">删除</button></div>`).join("")}</div><button class="ghost" type="button" data-action="add-task">+ 添加任务</button></div>`;
  showModal(modalShell(d.id ? "编辑调度队列" : "新建调度队列", body, '<button type="button" data-action="save-queue">保存</button><button class="ghost" type="button" data-action="close-modal">取消</button>'), true);
}

export function queueAddTimeSet() { queueDraft.timeSets.push({ id: "", enabled: true, days: [1, 2, 3, 4, 5], time: "05:30" }); renderQueueModal(); }
export function queueRemoveTimeSet(index) { syncQueueDraftFromDom(); queueDraft.timeSets.splice(index, 1); renderQueueModal(); }
export function queueAddTask() { syncQueueDraftFromDom(); queueDraft.tasks.push({ id: "", index: queueDraft.tasks.length, scriptInstanceId: "" }); renderQueueModal(); }
export function queueRemoveTask(index) { syncQueueDraftFromDom(); queueDraft.tasks.splice(index, 1); queueDraft.tasks.forEach((task, i) => task.index = i); renderQueueModal(); }
export function queueMoveTask(index, direction) { syncQueueDraftFromDom(); const other = index + direction; if (other < 0 || other >= queueDraft.tasks.length) return; [queueDraft.tasks[index], queueDraft.tasks[other]] = [queueDraft.tasks[other], queueDraft.tasks[index]]; queueDraft.tasks.forEach((task, i) => task.index = i); renderQueueModal(); }

export async function saveQueue() {
  syncQueueDraftFromDom();
  const draft = queueDraft;
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

export const actions = {
  "open-queue-modal": target => openQueueModal(target.dataset.id || ""),
  "edit-queue": target => openQueueModal(target.dataset.id),
  "delete-queue": target => deleteQueue(target.dataset.id, target.dataset.name),
  "save-queue": () => saveQueue(),
  "add-time-set": () => queueAddTimeSet(),
  "remove-time-set": target => queueRemoveTimeSet(+target.dataset.index),
  "add-task": () => queueAddTask(),
  "remove-task": target => queueRemoveTask(+target.dataset.index),
  "move-task-up": target => queueMoveTask(+target.dataset.index, -1),
  "move-task-down": target => queueMoveTask(+target.dataset.index, 1),
};
