import { api } from "../core/api.js";
import { $, $$ } from "../core/dom.js";
import { esc } from "../core/format.js";
import { isCurrent, schedule, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, toast } from "../core/ui.js";

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
    <div class="dispatch-cards">
    <section class="card"><div class="section-heading"><h3>手动执行脚本实例</h3><span class="muted">启用用户将自动依次运行</span></div><div class="form-grid dispatch-controls dispatch-script-controls"><div><label class="field-label" for="dc-script">脚本实例</label><select id="dc-script" data-testid="dispatch-script"><option value="">（选择脚本实例）</option>${scripts.map(script => `<option value="${script.id}">${esc(script.name)}</option>`).join("")}</select></div><div class="control-action"><button type="button" data-action="dispatch-script">执行</button></div></div></section>
    <section class="card"><div class="section-heading"><h3>手动执行调度队列</h3><span class="muted">按队列内顺序运行</span></div><div class="form-grid dispatch-controls dispatch-queue-controls"><div><label class="field-label" for="dc-queue">调度队列</label><select id="dc-queue"><option value="">（选择调度队列）</option>${queues.map(queue => `<option value="${queue.id}">${esc(queue.name)}</option>`).join("")}</select></div><div class="control-action"><button type="button" data-action="dispatch-queue">执行</button></div></div></section>
    </div>`);
  applyProgress();
  schedule(() => refreshDispatch(token), 2000, "dispatch", token);
}

async function refreshDispatch(token) {
  if (!isCurrent("dispatch", token)) return;
  try { const status = await api("GET", "/api/status"); if (isCurrent("dispatch", token)) updateRunning(status); }
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

export async function cancelRun(runId) {
  try { await api("POST", "/api/cancel", { runId }); toast("已发送取消请求"); }
  catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "dispatch-script": () => dispatchScript(),
  "dispatch-queue": () => dispatchQueue(),
  "cancel-run": target => cancelRun(target.dataset.id),
};
