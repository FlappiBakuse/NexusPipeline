import { api, hydrateIcons } from "../core/api.js";
import { $, $$ } from "../core/dom.js";
import { esc, scriptFallbackIcon, scriptPluginStatus, scriptPluginUnavailableMessage } from "../core/format.js";
import { pageHeader, selectField, switchControl, valueField } from "../core/forms.js";
import { selectControlMarkup, timeControlMarkup } from "../core/controls.js";
import { icon } from "../core/icons.js";
import { pagerMarkup, registerPager, replacePageOrder } from "../core/pager.js";
import { isCurrent, notifyAvailable, registerInterval, state } from "../core/state.js";
import { closeModal, confirmModal, modalShell, showModal } from "../core/modal.js";
import { navActive, render, setFieldError, clearFieldError, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { initDndList } from "../core/dnd.js";
import { pluginSlotMarkup, renderPluginSlots } from "../core/plugin-slots.js";

let queueDraft = null;
let queuePage = 1;
let nextTimer = null;
let queuePendingMerged = false;
let queuePendingDuplicateMerged = false;
let queueModalScroll = null;
let queueOpenTimeSets = null;
const QUEUE_PAGE_SIZE = 20;
const MAX_ENTITY_NAME_BYTES = 64;

if (typeof document !== "undefined") {
  document.addEventListener("change", event => {
    const target = event.target;
    if (!target?.matches?.("[data-ts-time]") || !queueDraft) return;
    const card = target.closest(".timeset-card");
    const timeSet = card ? queueDraft.timeSets[Number(card.dataset.tsIdx)] : null;
    const value = String(target.value || "").trim();
    if (timeSet && /^\d{2}:\d{2}$/u.test(value)) timeSet.time = value;
  });
}

export async function pageQueues(token) {
  if (!isCurrent("queues", token)) return;
  navActive("queues"); setTopbarTitle("调度队列");
  let queues, scripts, status;
  try { [queues, scripts, status] = await Promise.all([api("GET", "/api/queues"), api("GET", "/api/scripts"), api("GET", "/api/status")]); }
  catch (error) { render(`<div class="empty"><strong>加载队列失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("queues", token)) return;
  state.queues = queues; state.scripts = scripts; state.plugins = status.plugins || [];
  const atLimit = !!(state.limits && queues.length >= state.limits.maxQueues);
  const action = `<button class="primary" type="button" data-action="open-queue-modal" ${atLimit ? "disabled" : ""}>新建调度队列${atLimit ? `（${queues.length}/${state.limits.maxQueues}）` : ""}</button>`;
  const totalPages = Math.max(1, Math.ceil(queues.length / QUEUE_PAGE_SIZE));
  if (queuePage > totalPages) queuePage = totalPages;
  const pageItems = queues.slice((queuePage - 1) * QUEUE_PAGE_SIZE, queuePage * QUEUE_PAGE_SIZE);
  const content = queues.length ? `<section class="card list-surface"><div class="script-grid">
    ${pageItems.map(queue => queueCardMarkup(queue, scripts)).join("")}
    </div>${pagerMarkup("queues", queuePage, QUEUE_PAGE_SIZE, queues.length)}</section>` : '<div class="empty"><strong>暂无调度队列</strong><span>把多个脚本串成一个可重复执行的工作流。</span><a class="back-link" href="#/queues" data-action="open-queue-modal">新建调度队列</a></div>';
  render(pageHeader("调度管理", "调度队列", "把多个脚本串成可重复执行的工作流。", action) + content);
  await renderPluginSlots(document.querySelector("#view"));
  registerPager("queues", p => { queuePage = p; pageQueues(state.routeToken); });
  tickQueueNext();
  if (nextTimer) clearInterval(nextTimer);
  nextTimer = setInterval(tickQueueNext, 1000);
  registerInterval(nextTimer);
  $domIcons();
  hydrateIcons($("#view"));
  wireQueueDnd();
}

/** 拖拽排序：只改变当前分页区间，其他分页保持原位置与相对顺序。 */
function wireQueueDnd() {
  const list = $(".script-grid");
  if (!list) return;
  initDndList(list, { onDrop: (ids) => reorderQueues(ids) });
}

/** 把当前页新顺序写回全量列表，提交 PUT /api/queues/order。 */
async function reorderQueues(visibleIds) {
  const pageScrollTop = window.scrollY;
  const full = replacePageOrder(state.queues, queuePage, QUEUE_PAGE_SIZE, visibleIds);
  try {
    await api("PUT", "/api/queues/order", { ids: full.map(item => item.id) });
    toast("队列顺序已保存");
    await pageQueues(state.routeToken);
    restorePageScroll(pageScrollTop);
  } catch (error) {
    toast(error.message, "error");
    await pageQueues(state.routeToken);
    restorePageScroll(pageScrollTop);
  }
}

function restorePageScroll(top) {
  requestAnimationFrame(() => {
    window.scrollTo({ top, left: 0, behavior: "auto" });
    requestAnimationFrame(() => window.scrollTo({ top, left: 0, behavior: "auto" }));
  });
}

function queueCardMarkup(queue, scripts) {
  const firstTask = (queue.tasks || []).slice().sort((a, b) => a.index - b.index).find(item => scripts.some(script => script.id === item.scriptInstanceId));
  const firstScript = firstTask ? scripts.find(script => script.id === firstTask.scriptInstanceId) : null;
  const unavailableScripts = (queue.tasks || [])
    .map(task => scripts.find(script => script.id === task.scriptInstanceId))
    .filter(Boolean)
    .map(script => ({ script, status: scriptPluginStatus(script, state.plugins || []) }))
    .filter(item => item.status.specialized && !item.status.available);
  const missingScripts = unavailableScripts.filter(item => item.status.missing);
  const disabledScripts = unavailableScripts.filter(item => !item.status.missing);
  const unavailableBadge = [
    missingScripts.length
      ? `<span class="badge bad" title="${esc(missingScripts.map(item => scriptPluginUnavailableMessage(item.script, state.plugins || [])).join("；"))}">含 ${missingScripts.length} 个未知专项任务</span>`
      : "",
    disabledScripts.length
      ? `<span class="badge warn" title="${esc(disabledScripts.map(item => scriptPluginUnavailableMessage(item.script, state.plugins || [])).join("；"))}">含 ${disabledScripts.length} 个不可用专项任务</span>`
      : "",
  ].join("");
  const nextAt = queue.nextTrigger ? new Date(queue.nextTrigger).getTime() : 0;
  const timeBadge = queue.autoRunMode === "scheduled"
    ? `<span class="badge blue queue-next" data-next="${nextAt || ""}">${nextAt ? "正在计算倒计时" : "等待定时触发"}</span>`
    : queue.autoRunMode === "startup"
      ? '<span class="badge blue">将在下次启动开始运行</span>'
      : '<span class="badge blue" data-testid="queue-manual-badge">不自动运行</span>';
  const notifyBadge = notifyAvailable()
    ? `<span class="badge ${queue.notifyEnabled ? "ok" : "muted"}" data-testid="queue-notify">${queue.notifyEnabled ? "队列通知已开启" : "队列通知未开启"}</span>`
    : "";
  return `<article class="script-card queue-card" data-testid="queue-card" data-dnd-id="${esc(queue.id)}">
    <span class="drag-handle" role="button" tabindex="0" aria-label="拖拽排序（方向键调整顺序）" title="拖拽排序">${icon("grip")}</span>
    <img class="script-ico" src="${esc(scriptFallbackIcon)}" alt="" width="36" height="36" loading="lazy" data-icon-id="${firstScript ? esc(firstScript.id) : ""}">
    <div class="script-main">
      <button class="entity-link" type="button" data-action="edit-queue" data-id="${esc(queue.id)}" aria-label="编辑调度队列：${esc(queue.name)}"><span class="scroll-text"><span class="scroll-inner">${esc(queue.name)}</span></span></button>
      <div class="meta-line queue-meta"><span class="badge muted">${(queue.tasks || []).length} 个任务</span><span class="badge muted">${queue.completionAction && queue.completionAction !== "none" ? `完成后${queue.completionAction === "exit" ? "退出软件" : queue.completionAction === "sleep" ? "休眠" : queue.completionAction === "reboot" ? "重启" : "关机"}` : "完成后无操作"}</span>${unavailableBadge}${timeBadge}${notifyBadge}${pluginSlotMarkup("queues.list.badges", `queue-${queue.id}`, "queue-plugin-slot", { mode: "list", primaryId: queue.id })}</div>
    </div>
    <div class="queue-ops row-actions entity-actions">
      <button class="tertiary queue-edit" type="button" data-action="edit-queue-direct" data-id="${esc(queue.id)}">编辑队列</button>
      <button class="danger" type="button" data-action="delete-queue" data-id="${esc(queue.id)}" data-name="${esc(queue.name)}">删除队列</button>
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
  queueModalScroll = null;
  queueOpenTimeSets = null;
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
  // 按元素携带的 data-ts-idx（渲染下标，随拖拽移动）写回原数组项——DOM 顺序与数组顺序脱钩后仍正确。
  $$(".timeset-card").forEach(card => {
    const target = queueDraft.timeSets[+card.dataset.tsIdx]; if (!target) return;
    const enabled = card.querySelector("[data-ts-enable]"); if (enabled) target.enabled = enabled.getAttribute("aria-pressed") === "true";
    const time = card.querySelector("[data-ts-time]"); if (time) target.time = time.value.trim() || target.time;
    const dayButtons = Array.from(card.querySelectorAll("[data-ts-days]")).filter(input => input.getAttribute("aria-pressed") === "true");
    target.days = dayButtons.map(input => +input.dataset.day);
  });
  $$('[data-task-idx]').forEach(select => { const index = +select.dataset.taskIdx; if (queueDraft.tasks[index]) queueDraft.tasks[index].scriptInstanceId = select.value; });
}

function queueTaskOptions(scripts) {
  return [{ value: "", label: "（选择脚本实例）" }, ...(scripts || []).map(script => {
    const pluginStatus = scriptPluginStatus(script, state.plugins || []);
    const unavailable = pluginStatus.specialized && !pluginStatus.available;
    const unavailableMessage = unavailable ? scriptPluginUnavailableMessage(script, state.plugins || []) : "";
    const suffix = unavailable
      ? (pluginStatus.missing ? "（未知专项）" : "（专项插件不可用）")
      : (script.logStallTimeoutMinutes === -1 ? "（长时）" : "");
    return {
      value: script.id,
      label: `${script.name}${suffix}`,
      disabled: unavailable,
      title: unavailableMessage,
    };
  })];
}

function captureQueueOpenState() {
  if (!queueDraft) return;
  const cards = $$("#qm-timesets .timeset-card");
  if (!cards.length) return;
  queueOpenTimeSets = new Set(cards.filter(card => card.open).map(card => queueDraft.timeSets[+card.dataset.tsIdx]).filter(Boolean));
}

export function renderQueueModal(skipOpenCapture = false) {
  if (!skipOpenCapture) captureQueueOpenState();
  const previousBody = $(".modal-mask .modal-body");
  if (previousBody) queueModalScroll = { left: previousBody.scrollLeft, top: previousBody.scrollTop };
  syncQueueDraftFromDom();
  const d = queueDraft;
  const scripts = state.scripts;
  const days = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
  const l = state.limits || {};
  const timeSetAtLimit = !!(l.maxTimeSetsPerQueue && d.timeSets.length >= l.maxTimeSetsPerQueue);
  const body = `${valueField("qm-name", "队列名称 <span class='req'>*</span>", d.name)}
    <p class="muted helper-copy">队列名称最多 ${MAX_ENTITY_NAME_BYTES} 个 UTF-8 字节。</p>
    <div class="form-grid">${selectField("qm-mode", "自动运行方式", d.autoRunMode, [{ value: "none", label: "不运行" }, { value: "scheduled", label: "定时运行" }, { value: "startup", label: "启动时运行" }])}${selectField("qm-action", "运行完成操作", d.completionAction, [{ value: "none", label: "无操作" }, { value: "exit", label: "退出软件" }, { value: "sleep", label: "休眠" }, { value: "reboot", label: "重启" }, { value: "shutdown", label: "关机" }])}</div>
    <div ${notifyAvailable() ? "" : "hidden"}>${switchControl("qm-notify", "队列通知", "统一发送所有脚本状态，覆盖实例级设置", d.notifyEnabled, "toggle-qm-flag")}</div>
    <div class="subsection"><div class="section-heading"><h3>定时列表</h3><span class="muted">默认收起；展开后编辑周期与执行时间，拖拽左侧把手排序</span></div><div id="qm-timesets" class="timeset-list">${d.timeSets.map((timeSet, index) => `<details class="timeset-card compact-card" data-dnd-id="${index}" data-ts-idx="${index}" ${((queueOpenTimeSets ? queueOpenTimeSets.has(timeSet) : index === 0) ? "open" : "")}><summary class="timeset-summary"><span class="timeset-summary-main"><span class="drag-handle" role="button" tabindex="0" aria-label="拖拽排序（方向键调整顺序）" title="拖拽排序">${icon("grip")}</span><strong>定时 ${index + 1}</strong><span class="muted">${esc(timeSet.time || "未设置时间")} · ${timeSet.days.length ? `${timeSet.days.length} 天` : "未选周期"}</span></span><span class="timeset-summary-chevron" aria-hidden="true">⌄</span></summary><div class="timeset-details"><div class="timeset-body"><div class="timeset-layout"><div class="timeset-days"><label class="field-label">执行周期（可多选）</label><div class="days-btn-grid" role="group" aria-label="执行周期">${days.map((name, day) => `<button class="mode-toggle" type="button" data-action="toggle-ts-day" data-ts-days="${index}" data-day="${day}" aria-pressed="${timeSet.days.includes(day) ? "true" : "false"}" title="${esc(name)}" aria-label="${esc(name)}">${esc("日一二三四五六"[day])}</button>`).join("")}</div></div><div class="timeset-time"><label class="field-label" for="ts-time-${index}">执行时间</label>${timeControlMarkup(`ts-time-${index}`, timeSet.time, `data-ts-time="${index}"`, "执行时间")}</div></div><div class="timeset-actions"><button class="mode-toggle switch-control" type="button" data-action="toggle-ts-enable" data-ts-enable="${index}" data-toggle-text="false" aria-pressed="${timeSet.enabled ? "true" : "false"}" data-state="${timeSet.enabled ? "on" : "off"}"><span class="switch-track" aria-hidden="true"><span class="switch-thumb"></span></span><span class="sr-only" data-switch-state>${timeSet.enabled ? "已启用" : "已停用"}</span></button><button class="tertiary" type="button" data-action="remove-time-set" data-index="${index}">删除定时</button></div></div></div></details>`).join("")}</div><button class="ghost" type="button" data-action="add-time-set" ${timeSetAtLimit ? "disabled" : ""}>+ 添加定时${timeSetAtLimit ? `（${d.timeSets.length}/${l.maxTimeSetsPerQueue}）` : ""}</button></div>
    <div class="subsection"><div class="section-heading"><h3>任务列表</h3><span class="muted">按顺序先后执行，拖拽左侧把手排序；长时运行与标准运行不能混合编排</span></div>${d.tasks.length ? `<div class="tasks-body"><div id="qm-tasks">${d.tasks.slice().sort((a, b) => a.index - b.index).map((task, index) => `<div class="list-item task-row" data-dnd-id="${index}"><span class="drag-handle" role="button" tabindex="0" aria-label="拖拽排序（方向键调整顺序）" title="拖拽排序">${icon("grip")}</span>${selectControlMarkup(`qm-task-${index}`, task.scriptInstanceId, queueTaskOptions(scripts), `data-task-idx="${index}"`, `第 ${index + 1} 个任务：脚本实例`)}<button class="sm danger" type="button" data-action="remove-task" data-index="${index}">删除</button></div>`).join("")}</div></div>` : ""}<button class="ghost" type="button" data-action="add-task">+ 添加任务</button></div>`;
  showModal(modalShell(d.id ? "编辑调度队列" : "新建调度队列", body + pluginSlotMarkup("queues.editor.sections", "queues.editor.sections", "queue-editor-plugin-slot", { mode: d.id ? "edit" : "create", primaryId: d.id || "" }), '<button class="ghost" type="button" data-action="close-modal">取消</button><button class="primary" type="button" data-action="save-queue">保存</button>'), true, true);
  void renderPluginSlots(document);
  const restoreModalScroll = () => {
    if (!queueModalScroll) return;
    const nextBody = $(".modal-mask .modal-body");
    if (!nextBody) return;
    nextBody.scrollLeft = queueModalScroll.left;
    nextBody.scrollTop = queueModalScroll.top;
  };
  requestAnimationFrame(() => { restoreModalScroll(); requestAnimationFrame(restoreModalScroll); });
  // 定时列表与任务列表拖拽排序（复用 core/dnd.js；DOM 已重排，onDrop 按 data-dnd-id 重排数组）。
  initDndList($("#qm-timesets"), { onDrop: ids => reorderTimeSets(ids) });
  // 任务列表为空时不渲染列表容器（qm-tasks 节点不存在），须条件注册拖拽。
  if (d.tasks.length) initDndList($("#qm-tasks"), { onDrop: ids => reorderTasks(ids) });
}

/** 定时列表拖拽排序：值已由 sync 按 data-ts-idx 写回原数组项，按 data-dnd-id（渲染下标）顺序重排数组；
 *  随后把 DOM 卡的 data-ts-idx 与新数组下标对齐——renderQueueModal 开头 sync 依赖它，避免重排后旧索引错写值。 */
function reorderTimeSets(ids) {
  syncQueueDraftFromDom();
  queueDraft.timeSets = ids.map(id => queueDraft.timeSets[+id]);
  $$("#qm-timesets .timeset-card").forEach((card, i) => { card.dataset.tsIdx = String(i); });
  renderQueueModal();
}

/** 任务列表拖拽排序：同定时列表；index 字段随之重排（执行顺序）。 */
function reorderTasks(ids) {
  syncQueueDraftFromDom();
  queueDraft.tasks = ids.map(id => queueDraft.tasks[+id]);
  queueDraft.tasks.forEach((task, i) => task.index = i);
  $$("#qm-tasks .task-row").forEach((row, i) => {
    const select = row.querySelector("[data-task-idx]");
    if (select) select.dataset.taskIdx = String(i);
  });
  renderQueueModal();
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
export function queueRemoveTimeSet(index) { syncQueueDraftFromDom(); captureQueueOpenState(); queueDraft.timeSets.splice(index, 1); renderQueueModal(true); }
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

export async function saveQueue() {
  syncQueueDraftFromDom();
  queuePendingDuplicateMerged = false;
  const draft = queueDraft;
  draft.timeSets.forEach((timeSet, index) => { const tsEnable = $(`[data-ts-enable='${index}']`); if (tsEnable) timeSet.enabled = tsEnable.getAttribute("aria-pressed") === "true"; timeSet.time = $(`[data-ts-time='${index}']`)?.value.trim() || "05:30"; timeSet.days = $$(`[data-ts-days='${index}']`).filter(input => input.getAttribute("aria-pressed") === "true").map(input => +input.dataset.day); });
  draft.timeSets = draft.timeSets.filter(timeSet => timeSet.days.length);
  draft.tasks = draft.tasks.map((task, index) => ({ ...task, index, scriptInstanceId: $(`[data-task-idx='${index}']`)?.value || task.scriptInstanceId })).filter(task => task.scriptInstanceId);
  queuePendingDuplicateMerged = mergeDuplicateTasks();
  if (!draft.name) { setFieldError("qm-name", "队列名称不能为空"); toast("队列名称不能为空", "error"); return; }
  clearFieldError("qm-name");
  if (!draft.tasks.length) { toast("任务列表为空，请至少添加一个脚本任务", "error"); return; }
  const unavailableScript = draft.tasks
    .map(task => state.scripts.find(script => script.id === task.scriptInstanceId))
    .find(script => script && scriptPluginStatus(script, state.plugins || []).specialized && !scriptPluginStatus(script, state.plugins || []).available);
  if (unavailableScript) {
    toast(scriptPluginUnavailableMessage(unavailableScript, state.plugins || []) + "；请先移除该任务后再保存队列", "error");
    return;
  }
  const l = state.limits || {};
  const nameBytes = new TextEncoder().encode(draft.name).length;
  if (nameBytes > MAX_ENTITY_NAME_BYTES) { setFieldError("qm-name", `队列名称最多 ${MAX_ENTITY_NAME_BYTES} 字节`); toast(`队列名称最多 ${MAX_ENTITY_NAME_BYTES} 字节`, "error"); return; }
  if (l.maxTimeSetsPerQueue && draft.timeSets.length > l.maxTimeSetsPerQueue) { toast(`定时列表已达上限（${draft.timeSets.length}/${l.maxTimeSetsPerQueue}）`, "error"); return; }
  const totalUsers = queueTotalUsers();
  if (l.maxQueueTotalUsers && totalUsers > l.maxQueueTotalUsers) { toast(`任务列表的启用用户总数已达上限（${totalUsers}/${l.maxQueueTotalUsers}）`, "error"); return; }
  // 长时/普通混排拦截（与后端 CheckQueueMix 一致；长时脚本可能持续运行并阻塞队列后续任务）
  const taskScripts = draft.tasks.map(task => state.scripts.find(item => item.id === task.scriptInstanceId)).filter(Boolean);
  const hasLong = taskScripts.some(script => script.logStallTimeoutMinutes === -1);
  const hasNormal = taskScripts.some(script => script.logStallTimeoutMinutes !== -1);
  if (hasLong && hasNormal) { toast("队列不能混合编排长时脚本（日志无更新上限为 -1）与普通脚本实例，请分开建立队列", "error"); return; }
  const mergedCount = mergeTimeSets();
  queuePendingMerged = mergedCount > 0;
  if (hasTimeGap()) {
    showModal(modalShell("定时间隔警告", `<p class="modal-copy">存在间隔低于10分钟的定时任务，如果之前的定时任务还未完成，之后的定时任务可能会忽略，确定吗？</p>`, '<button class="primary" type="button" data-action="confirm-timegap-save">确定</button><button class="ghost" type="button" data-action="cancel-timegap">取消</button>'));
    return;
  }
  await doSaveQueue(queuePendingMerged, queuePendingDuplicateMerged);
}

/** 按任务排序去重：每个脚本实例只保留排序列表中的第一项。 */
function mergeDuplicateTasks() {
  const seen = new Set();
  const distinct = [];
  let removed = 0;
  for (const task of queueDraft.tasks.slice().sort((a, b) => a.index - b.index)) {
    if (seen.has(task.scriptInstanceId)) {
      removed++;
      continue;
    }
    seen.add(task.scriptInstanceId);
    task.index = distinct.length;
    distinct.push(task);
  }
  queueDraft.tasks = distinct;
  return removed > 0;
}

/** 定时列表合并：同启用状态、执行时间相同的列表取星期并集，保留排序第一项。 */
function mergeTimeSets() {
  const firstByKey = new Map();
  const merged = [];
  let removed = 0;
  for (const timeSet of queueDraft.timeSets) {
    timeSet.days = [...new Set(Array.isArray(timeSet.days) ? timeSet.days : [])].sort((a, b) => a - b);
    const key = `${timeSet.enabled}|${timeSet.time}`;
    const first = firstByKey.get(key);
    if (first) {
      first.days = [...new Set([...first.days, ...timeSet.days])].sort((a, b) => a - b);
      removed++;
      continue;
    }
    firstByKey.set(key, timeSet);
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

async function doSaveQueue(merged, duplicateMerged) {
  const draft = queueDraft;
  try {
    if (draft.id) await api("PUT", "/api/queues/" + draft.id, draft);
    else await api("POST", "/api/queues", draft);
    closeModal();
    const messages = [];
    if (duplicateMerged) messages.push("重复脚本实例任务已合并");
    if (merged) messages.push("重复定时列表已合并");
    toast(messages.join("；") || "调度队列已保存");
    queuePendingMerged = false;
    queuePendingDuplicateMerged = false;
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
  "edit-queue-direct": target => openQueueModal(target.dataset.id),
  "delete-queue": target => deleteQueue(target.dataset.id, target.dataset.name),
  "confirm-delete-queue": target => withBusy(target, () => confirmDeleteQueue(target.dataset.id, target.dataset.name)),
  "save-queue": target => withBusy(target, () => saveQueue()),
  "confirm-timegap-save": () => doSaveQueue(queuePendingMerged, queuePendingDuplicateMerged),
  "cancel-timegap": () => cancelTimeGap(),
  "add-time-set": () => queueAddTimeSet(),
  "remove-time-set": target => queueRemoveTimeSet(+target.dataset.index),
  "add-task": () => queueAddTask(),
  "remove-task": target => queueRemoveTask(+target.dataset.index),
  "toggle-qm-flag": () => { const btn = $("#qm-notify"); if (btn) togglePressed(btn); },
  "toggle-ts-day": target => togglePressed(target),
  "toggle-ts-enable": target => togglePressed(target),
};

function togglePressed(btn) {
  btn.setAttribute("aria-pressed", btn.getAttribute("aria-pressed") === "true" ? "false" : "true");
}
