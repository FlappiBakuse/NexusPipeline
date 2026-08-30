import { api } from "../core/api.js";
import { $, $$ } from "../core/dom.js";
import { pageHeader, selectField, systemActionCard } from "../core/forms.js";
import { esc, scriptPluginStatus, scriptPluginUnavailableMessage } from "../core/format.js";
import { closeModal, confirmModal } from "../core/modal.js";
import { isCurrent, schedule, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, startSystemActionCountdown, toast, withBusy } from "../core/ui.js";
import { pluginSlotMarkup, renderPluginSlots } from "../core/plugin-slots.js";
import { disposePluginSlot, notifyPluginPageUpdated } from "../core/plugin-runtime.js";
import { selectControlMarkup } from "../core/controls.js";

const LOG_NEAR_BOTTOM_PX = 40;
const LOG_QUIET_PERIOD_MS = 700;

function executionPreviewLayoutEnabled(plugins = state.plugins) {
  const plugin = (plugins || []).find(item => Array.isArray(item.capabilities)
    && item.capabilities.some(capability => String(capability || "").toLowerCase() === "execution-preview-client"));
  return plugin?.configuredEnabled === true
    && plugin?.runtimeEnabled === true
    && plugin?.hasFrontend === true;
}

function syncRunningLayout(plugins = state.plugins) {
  const enabled = executionPreviewLayoutEnabled(plugins);
  document.querySelectorAll("#running-list .running-item-content").forEach(content => {
    content.classList.toggle("has-execution-preview", enabled);
  });
}

function logEntries(record) {
  if (Array.isArray(record.logEntries)) return record.logEntries;
  return (record.logTail || []).map((text, index) => ({ sequence: index + 1, level: "info", text }));
}

function logLevelClass(level) {
  const value = String(level || "info").toLowerCase();
  return ["debug", "info", "warn", "error", "fatal"].includes(value) ? value : "info";
}

function logLineMarkup(entry) {
  return `<span class="run-log-line run-log-${logLevelClass(entry.level)}">${esc(entry.text || "")}</span>`;
}

function logMarkup(record) {
  const entries = logEntries(record);
  return entries.length
    ? entries.map(logLineMarkup).join("")
    : '<span class="run-log-empty">(暂无日志输出)</span>';
}

function attachLogInteraction(logbox) {
  if (!logbox || logbox.dataset.logBound) return;
  logbox.dataset.logBound = "1";
  logbox._logState = { lastSequence: Number(logbox.dataset.logSequence) || 0, lastUserScrollAt: 0, isNearBottom: true, programmatic: false };
  const mark = () => {
    const state = logbox._logState;
    if (!state || state.programmatic) return;
    state.lastUserScrollAt = Date.now();
    state.isNearBottom = nearBottom(logbox);
  };
  ["scroll", "wheel", "pointerdown", "touchmove"].forEach(type => logbox.addEventListener(type, mark, { passive: true }));
}

function updateLogState(logbox, record, initial = false) {
  attachLogInteraction(logbox);
  const state = logbox._logState;
  const entries = logEntries(record);
  const newest = entries.reduce((max, entry) => Math.max(max, Number(entry.sequence) || 0), 0);
  if (initial || !Array.isArray(record.logEntries)) {
    logbox.innerHTML = logMarkup(record);
    state.lastSequence = newest;
    state.isNearBottom = true;
    state.programmatic = true;
    logbox.scrollTop = logbox.scrollHeight;
    state.programmatic = false;
    return;
  }
  const pending = entries.filter(entry => (Number(entry.sequence) || 0) > state.lastSequence);
  if (!pending.length) return;
  const shouldFollow = state.isNearBottom && Date.now() - state.lastUserScrollAt >= LOG_QUIET_PERIOD_MS;
  const empty = logbox.querySelector(".run-log-empty");
  if (empty) empty.remove();
  pending.forEach(entry => logbox.insertAdjacentHTML("beforeend", logLineMarkup(entry)));
  state.lastSequence = Math.max(state.lastSequence, ...pending.map(entry => Number(entry.sequence) || 0));
  if (shouldFollow) {
    state.programmatic = true;
    logbox.scrollTop = logbox.scrollHeight;
    state.programmatic = false;
  }
}

/** 单个运行任务卡片 HTML（新任务插入用；已有任务走 updateRunningItem 局部更新，不重建 DOM）。 */
function runningItemMarkup(record) {
  const mode = record.kind === "queue" ? "queue" : "script";
  const newestSequence = logEntries(record).reduce((max, entry) => Math.max(max, Number(entry.sequence) || 0), 0);
  return `<div class="list-item-head"><div><div class="list-item-title"><strong>${esc(record.targetName)}</strong><span class="badge ${record.kind === "queue" ? "blue" : "muted"}">${record.kind === "queue" ? "调度队列" : "脚本实例"}</span><span class="badge muted">${record.mode === "auto" ? "自动" : "手动"}</span>${record.kind === "queue" ? `<span class="muted done-count">${record.doneTasks}/${record.totalTasks} 项</span>` : ""}</div></div><button class="sm danger" type="button" data-action="cancel-run" data-id="${record.id}">取消运行</button></div>
    <div class="qk-row">当前：${esc(record.currentScriptName || "-")} ${esc(record.currentStatus || "")} · 第 ${record.currentAttempt}/${record.currentMaxAttempts} 次</div>${record.persistenceWarning ? `<div class="qk-row"><span class="badge warn">历史保存警告</span> ${esc(record.persistenceWarning)}</div>` : ""}
    <div class="progress-line"><div data-progress="0"></div></div>
    <div class="running-item-content"><pre class="logbox run-log run-terminal" data-log-sequence="${newestSequence}">${logMarkup(record)}</pre>${pluginSlotMarkup("dispatch.running.sidecar", "dispatch.running.sidecar", "running-sidecar", { mode, primaryId: record.id })}</div>`;
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
  return element.scrollHeight - element.scrollTop - element.clientHeight < LOG_NEAR_BOTTOM_PX;
}

/** 局部更新单个运行任务（）：只更新状态行/进度/日志文本，不重建 DOM——保留取消按钮焦点与日志选区。 */
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
    updateLogState(logbox, record);
  }
}

/** 运行面板局部更新（）：按 runId 增删改任务卡片，标题计数经 aria-live 播报，替代整块 innerHTML。 */
function updateRunning(status) {
  const panel = $("#dispatch-running");
  if (!panel) return [];
  const running = status.running || [];
  const head = panel.querySelector(".section-heading h3");
  if (head) head.textContent = `正在运行（${running.length}）`;
  const list = $("#running-list", panel);
  if (!list) return [];
  const existing = new Map();
  list.querySelectorAll(".running-item").forEach(item => existing.set(item.dataset.runId, item));
  const seen = new Set();
  const added = [];
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
      added.push(el);
      if (empty) empty.remove();
      list.appendChild(el);
    }
  });
  existing.forEach((item, id) => {
    if (seen.has(id)) return;
    const sidecar = item.querySelector('[data-plugin-slot="dispatch.running.sidecar"]');
    if (sidecar) void disposePluginSlot(sidecar);
    item.remove();
  });
  if (!running.length && !list.querySelector(".empty")) {
    const emptyEl = document.createElement("div");
    emptyEl.className = "empty";
    emptyEl.innerHTML = '<strong>当前没有正在运行的任务</strong>选择脚本或队列后，可以在这里查看实时状态。';
    list.appendChild(emptyEl);
  }
  list.querySelectorAll(".run-log").forEach(attachLogInteraction);
  syncRunningLayout(status.plugins || state.plugins);
  return added;
}

function updateSystemAction(status) {
  const area = $("#system-action-area");
  if (!area) return;
  area.innerHTML = systemActionCard(status.systemAction);
  startSystemActionCountdown();
}

function dispatchScriptOption(script) {
  const pluginStatus = scriptPluginStatus(script, state.plugins || []);
  const unavailable = pluginStatus.specialized && !pluginStatus.available;
  const unavailableMessage = unavailable ? scriptPluginUnavailableMessage(script, state.plugins || []) : "";
  const unavailableLabel = pluginStatus.missing ? "（未知专项）" : "（专项插件不可用）";
  return { value: script.id, label: `${script.name}${unavailable ? unavailableLabel : ""}`, disabled: unavailable, title: unavailableMessage };
}

export async function pageDispatch(token) {
  if (!isCurrent("dispatch", token)) return;
  navActive("dispatch"); setTopbarTitle("调度中心");
  let status, scripts, queues;
  try { [status, scripts, queues] = await Promise.all([api("GET", "/api/status"), api("GET", "/api/scripts"), api("GET", "/api/queues")]); }
  catch (error) { render(`<div class="empty"><strong>加载调度中心失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("dispatch", token)) return;
  state.scripts = scripts; state.queues = queues; state.plugins = status.plugins || [];
  render(pageHeader("调度中心", "调度中心", "手动启动任务，观察实时输出并及时取消运行。") + pluginSlotMarkup("dispatch.cards", "dispatch.cards") + `
    <div id="system-action-area"></div>
    <section class="content-section list-surface" id="dispatch-running" data-testid="dispatch-running"><div class="section-heading"><h3>正在运行（${(status.running || []).length}）</h3><span class="muted">每 1 秒更新</span></div><div id="running-list">${runningMarkup(status.running || [])}</div></section>${pluginSlotMarkup("dispatch.running.badges", "dispatch.running.badges")}
    <section class="content-section" aria-labelledby="dispatch-run-heading"><div class="section-heading"><h3 id="dispatch-run-heading">开始一次运行</h3><span class="muted">选择目标后立即加入运行列表</span></div>
      <div class="dispatch-runbar">
        ${selectField("dc-kind", "目标类型", "script", [{ value: "script", label: "脚本实例" }, { value: "queue", label: "调度队列" }], 'data-action="dispatch-kind"')}
        <div class="field" id="dc-script-wrap"><label class="field-label" for="dc-script-trigger">脚本实例</label>${selectControlMarkup("dc-script", "", [{ value: "", label: "（选择脚本实例）" }, ...scripts.map(dispatchScriptOption)], 'data-testid="dispatch-script"', "脚本实例")}</div>
        <div class="field" id="dc-queue-wrap" hidden><label class="field-label" for="dc-queue-trigger">调度队列</label>${selectControlMarkup("dc-queue", "", [{ value: "", label: "（选择调度队列）" }, ...queues.map(queue => ({ value: queue.id, label: queue.name }))], "", "调度队列")}</div>
        <div class="control-action"><button id="dc-run" class="primary" type="button" data-action="dispatch-current" data-testid="dispatch-run">执行脚本</button></div>
      </div>
    </section>${pluginSlotMarkup("dispatch.run.sections", "dispatch.run.sections")}`);
  applyProgress();
  document.querySelectorAll("#running-list .run-log").forEach(attachLogInteraction);
  syncRunningLayout(state.plugins);
  updateSystemAction(status);
  await renderPluginSlots(document.querySelector("#view"));
  schedule(() => refreshDispatch(token), 1000, "dispatch", token);
}

async function refreshDispatch(token) {
  if (!isCurrent("dispatch", token)) return;
  try {
    const status = await api("GET", "/api/status");
    if (isCurrent("dispatch", token)) {
      const added = updateRunning(status);
      updateSystemAction(status);
      for (const item of added) await renderPluginSlots(item);
      await notifyPluginPageUpdated({
        hash: "dispatch",
        page: "dispatch",
        segments: ["dispatch"],
        token,
        container: document.querySelector("#view"),
      });
    }
  }
  catch (error) { if (isCurrent("dispatch", token)) toast("状态更新失败：" + error.message, "error"); }
  schedule(() => refreshDispatch(token), 1000, "dispatch", token);
}

export async function dispatchScript() {
  const id = $("#dc-script")?.value;
  if (!id) { toast("请选择脚本实例", "error"); return; }
  const script = (state.scripts || []).find(item => item.id === id);
  const unavailableMessage = script ? scriptPluginUnavailableMessage(script, state.plugins || []) : "";
  if (unavailableMessage) { toast(unavailableMessage, "error"); return; }
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
