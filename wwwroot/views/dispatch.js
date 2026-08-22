import { api } from "../core/api.js";
import { $, $$ } from "../core/dom.js";
import { pageHeader, systemActionCard } from "../core/forms.js";
import { esc } from "../core/format.js";
import { closeModal, confirmModal } from "../core/modal.js";
import { isCurrent, schedule, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, startSystemActionCountdown, toast, withBusy } from "../core/ui.js";

/** 单个运行任务卡片 HTML（新任务插入用；已有任务走 updateRunningItem 局部更新，不重建 DOM）。 */
function runningItemMarkup(record) {
  return `<div class="list-item-head"><div><div class="list-item-title"><strong>${esc(record.targetName)}</strong><span class="badge ${record.kind === "queue" ? "blue" : "muted"}">${record.kind === "queue" ? "调度队列" : "脚本实例"}</span><span class="badge muted">${record.mode === "auto" ? "自动" : "手动"}</span>${record.kind === "queue" ? `<span class="muted done-count">${record.doneTasks}/${record.totalTasks} 项</span>` : ""}</div></div><button class="sm danger" type="button" data-action="cancel-run" data-id="${record.id}">取消</button></div>
    <div class="qk-row">当前：${esc(record.currentScriptName || "-")} ${esc(record.currentStatus || "")} · 第 ${record.currentAttempt}/${record.currentMaxAttempts} 次</div>
    <div class="progress-line"><div data-progress="0"></div></div>
    <pre class="logbox run-log">${esc((record.logTail || []).join("\n")) || "(暂无日志输出)"}</pre>`;
}

function runningMarkup(running) {
  if (!running.length) return '<div class="empty"><strong>当前没有正在运行的任务</strong>选择脚本或队列后，可以在这里查看实时状态。</div>';
  return running.map(record => `<article class="list-item running-item" data-run-id="${esc(record.id)}">${runningItemMarkup(record)}</article>`).join("");
}

function applyProgress(root = document) {
  root.querySelectorAll("[data-progress]").forEach(element => {
    element.style.width = `${Math.max(0, Math.min(100, Number(element.dataset.progress) || 0))}%`;
  });
}

/** 判定日志框是否贴近底部（贴底时跟随自动滚动；用户上翻阅读时不打扰）。 */
function nearBottom(element) {
  return element.scrollHeight - element.scrollTop - element.clientHeight < 40;
}

/** 局部更新单个运行任务（v0.7.3，KN-16）：只更新状态行/进度/日志文本，不重建 DOM——保留取消按钮焦点与日志选区。 */
function updateRunningItem(el, record) {
  const qk = el.querySelector(".qk-row");
  if (qk) qk.textContent = `当前：${record.currentScriptName || "-"} ${record.currentStatus || ""} · 第 ${record.currentAttempt}/${record.currentMaxAttempts} 次`;
  const counter = el.querySelector(".done-count");
  if (counter && record.kind === "queue") counter.textContent = `${record.doneTasks}/${record.totalTasks} 项`;
  const prog = el.querySelector("[data-progress]");
  if (prog) {
    prog.dataset.progress = String(record.kind === "queue" && record.totalTasks
      ? Math.round(record.doneTasks / record.totalTasks * 100)
      : record.currentAttempt ? Math.round(record.currentAttempt / record.currentMaxAttempts * 100) : 0);
    prog.style.width = `${Math.max(0, Math.min(100, Number(prog.dataset.progress) || 0))}%`;
  }
  const logbox = el.querySelector(".run-log");
  if (logbox) {
    const stick = nearBottom(logbox);
    const text = (record.logTail || []).join("\n");
    logbox.textContent = text || "(暂无日志输出)";
    if (stick) logbox.scrollTop = logbox.scrollHeight;
  }
}

/** 运行面板局部更新（v0.7.3，KN-16）：按 runId 增删改任务卡片，标题计数经 aria-live 播报，替代整块 innerHTML。 */
function updateRunning(status) {
  const panel = $("#dispatch-running");
  if (!panel) return;
  const running = status.running || [];
  const head = panel.querySelector(".section-heading h3");
  if (head) head.textContent = `正在运行（${running.length}）`;
  const list = $("#running-list", panel);
  if (!list) return;
  const existing = new Map();
  list.querySelectorAll(".running-item").forEach(item => existing.set(item.dataset.runId, item));
  const seen = new Set();
  const empty = list.querySelector(".empty");
  running.forEach(record => {
    seen.add(record.id);
    const prev = existing.get(record.id);
    if (prev) {
      updateRunningItem(prev, record);
    } else {
      const el = document.createElement("article");
      el.className = "list-item running-item";
      el.dataset.runId = record.id;
      el.innerHTML = runningItemMarkup(record);
      applyProgress(el);
      if (empty) empty.remove();
      list.appendChild(el);
    }
  });
  existing.forEach((item, id) => { if (!seen.has(id)) item.remove(); });
  if (!running.length && !list.querySelector(".empty")) {
    const emptyEl = document.createElement("div");
    emptyEl.className = "empty";
    emptyEl.innerHTML = '<strong>当前没有正在运行的任务</strong>选择脚本或队列后，可以在这里查看实时状态。';
    list.appendChild(emptyEl);
  }
}

function updateSystemAction(status) {
  const area = $("#system-action-area");
  if (!area) return;
  area.innerHTML = systemActionCard(status.systemAction);
  startSystemActionCountdown();
}

export async function pageDispatch(token) {
  if (!isCurrent("dispatch", token)) return;
  navActive("dispatch"); setTopbarTitle("调度中心");
  let status, scripts, queues;
  try { [status, scripts, queues] = await Promise.all([api("GET", "/api/status"), api("GET", "/api/scripts"), api("GET", "/api/queues")]); }
  catch (error) { render(`<div class="empty"><strong>加载调度中心失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("dispatch", token)) return;
  state.scripts = scripts; state.queues = queues;
  render(pageHeader("调度中心", "调度中心", "手动启动任务，观察实时输出并及时取消运行。") + `
    <div id="system-action-area"></div>
    <section class="content-section list-surface" id="dispatch-running" data-testid="dispatch-running"><div class="section-heading"><h3>正在运行（${(status.running || []).length}）</h3><span class="muted">每 2 秒更新</span></div><div id="running-list">${runningMarkup(status.running || [])}</div></section>
    <section class="content-section" aria-labelledby="dispatch-run-heading"><div class="section-heading"><h3 id="dispatch-run-heading">开始一次运行</h3><span class="muted">选择目标后立即加入运行列表</span></div>
      <div class="dispatch-runbar">
        <div class="field"><label class="field-label" for="dc-kind">目标类型</label><select id="dc-kind" data-action="dispatch-kind"><option value="script">脚本实例</option><option value="queue">调度队列</option></select></div>
        <div class="field" id="dc-script-wrap"><label class="field-label" for="dc-script">脚本实例</label><select id="dc-script" data-testid="dispatch-script"><option value="">（选择脚本实例）</option>${scripts.map(script => `<option value="${esc(script.id)}">${esc(script.name)}</option>`).join("")}</select></div>
        <div class="field" id="dc-queue-wrap" hidden><label class="field-label" for="dc-queue">调度队列</label><select id="dc-queue"><option value="">（选择调度队列）</option>${queues.map(queue => `<option value="${esc(queue.id)}">${esc(queue.name)}</option>`).join("")}</select></div>
        <div class="control-action"><button id="dc-run" class="primary" type="button" data-action="dispatch-current">执行脚本</button></div>
      </div>
    </section>`);
  applyProgress();
  updateSystemAction(status);
  schedule(() => refreshDispatch(token), 2000, "dispatch", token);
}

async function refreshDispatch(token) {
  if (!isCurrent("dispatch", token)) return;
  try { const status = await api("GET", "/api/status"); if (isCurrent("dispatch", token)) { updateRunning(status); updateSystemAction(status); } }
  catch (error) { if (isCurrent("dispatch", token)) toast("状态更新失败：" + error.message, "error"); }
  schedule(() => refreshDispatch(token), 2000, "dispatch", token);
}

export async function dispatchScript() {
  const id = $("#dc-script")?.value;
  if (!id) { toast("请选择脚本实例", "error"); return; }
  try { await api("POST", "/api/dispatch/script", { scriptId: id, mode: "manual" }); toast("已开始执行"); }
  catch (error) { toast(error.message, "error"); }
}

export async function dispatchQueue() {
  const id = $("#dc-queue")?.value;
  if (!id) { toast("请选择调度队列", "error"); return; }
  try { await api("POST", "/api/dispatch/queue", { queueId: id, mode: "manual" }); toast("已开始执行"); }
  catch (error) { toast(error.message, "error"); }
}

export function dispatchKindChange(target) {
  const kind = target.value === "queue" ? "queue" : "script";
  const scriptWrap = $("#dc-script-wrap");
  const queueWrap = $("#dc-queue-wrap");
  const runButton = $("#dc-run");
  if (scriptWrap) scriptWrap.hidden = kind !== "script";
  if (queueWrap) queueWrap.hidden = kind !== "queue";
  if (runButton) runButton.textContent = kind === "queue" ? "执行队列" : "执行脚本";
}

export async function dispatchCurrent() {
  const kind = $("#dc-kind")?.value || "script";
  if (kind === "queue") return dispatchQueue();
  return dispatchScript();
}

export function cancelRun(runId) {
  confirmModal("取消运行", "当前任务将被终止；如果这是调度队列，后续任务也不会继续执行。确定取消吗？", "confirm-cancel-run", { id: runId });
}

export async function confirmCancelRun(runId) {
  try { await api("POST", "/api/cancel", { runId }); closeModal(); toast("已发送取消请求"); }
  catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "dispatch-script": target => withBusy(target, () => dispatchScript()),
  "dispatch-queue": target => withBusy(target, () => dispatchQueue()),
  "dispatch-current": target => withBusy(target, () => dispatchCurrent()),
  "dispatch-kind": target => dispatchKindChange(target),
  "cancel-run": target => cancelRun(target.dataset.id),
  "confirm-cancel-run": target => withBusy(target, () => confirmCancelRun(target.dataset.id)),
};
