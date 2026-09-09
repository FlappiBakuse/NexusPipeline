import { api } from "../core/api.js";
import { $, $$ } from "../core/dom.js";
import { pageHeader, selectField, systemActionCard } from "../core/forms.js";
import { esc, scriptPluginStatus, scriptPluginUnavailableMessage } from "../core/format.js";
import { closeModal, confirmModal, modalShell, showModal } from "../core/modal.js";
import { isCurrent, schedule, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, startSystemActionCountdown, toast, withBusy } from "../core/ui.js";
import { pluginSlotMarkup, renderPluginSlots } from "../core/plugin-slots.js";
import { disposePluginSlot, notifyPluginPageUpdated } from "../core/plugin-runtime.js";
import { selectControlMarkup } from "../core/controls.js";
import { t } from "../core/i18n.js";

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
    : `<span class="run-log-empty">(${t("ui.no_log_output")})</span>`;
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
  return `<div class="list-item-head"><div><div class="list-item-title"><strong>${esc(record.targetName)}</strong><span class="badge ${record.kind === "queue" ? "blue" : "muted"}">${t(record.kind === "queue" ? "ui.schedule_queues" : "ui.script_instance")}</span><span class="badge muted">${t(record.mode === "auto" ? "ui.automatic" : "ui.manual")}</span>${record.kind === "queue" ? `<span class="muted done-count">${record.doneTasks}/${record.totalTasks} ${t("ui.items_64728a77")}</span>` : ""}</div></div><button class="sm danger" type="button" data-action="cancel-run" data-id="${record.id}">${t("ui.cancel_run")}</button></div>
    <div class="qk-row">${t("ui.current_value_value_attempt_value_value", { script: record.currentScriptName || "-", status: record.currentStatus || "", attempt: record.currentAttempt, max: record.currentMaxAttempts })}</div>${record.persistenceWarning ? `<div class="qk-row"><span class="badge warn">${t("ui.history_persistence_warning")}</span> ${esc(record.persistenceWarning)}</div>` : ""}
    <div class="progress-line"><div data-progress="0"></div></div>
    <div class="running-item-content"><pre class="logbox run-log run-terminal" data-log-sequence="${newestSequence}">${logMarkup(record)}</pre>${pluginSlotMarkup("dispatch.running.sidecar", "dispatch.running.sidecar", "running-sidecar", { mode, primaryId: record.id })}</div>`;
}

function runningMarkup(running) {
  if (!running.length) return `<div class="empty"><strong>${t("ui.no_tasks_are_running")}</strong>${t("ui.choose_a_script_or_queue_to_view_live_status_here")}</div>`;
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
  if (qk) qk.textContent = t("ui.current_value_value_attempt_value_value", { script: record.currentScriptName || "-", status: record.currentStatus || "", attempt: record.currentAttempt, max: record.currentMaxAttempts });
  const counter = el.querySelector(".done-count");
  if (counter && record.kind === "queue") counter.textContent = `${record.doneTasks}/${record.totalTasks} ${t("ui.items_64728a77")}`;
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
  if (head) head.textContent = `${t("ui.running")} (${running.length})`;
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
    emptyEl.innerHTML = `<strong>${t("ui.no_tasks_are_running")}</strong>${t("ui.choose_a_script_or_queue_to_view_live_status_here")}`;
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
  const unavailableLabel = pluginStatus.missing ? t("ui.unknown_specialized_plugin_c960d836") : t("ui.specialized_plugin_unavailable_faec8375");
  return { value: script.id, label: `${script.name}${unavailable ? unavailableLabel : ""}`, disabled: unavailable, title: unavailableMessage };
}

export async function pageDispatch(token) {
  if (!isCurrent("dispatch", token)) return;
  navActive("dispatch"); setTopbarTitle(t("ui.scheduler"));
  let status, scripts, queues;
  try { [status, scripts, queues] = await Promise.all([api("GET", "/api/status"), api("GET", "/api/scripts"), api("GET", "/api/queues")]); }
  catch (error) { render(`<div class="empty"><strong>${t("ui.failed_to_load_dispatch")}</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("dispatch", token)) return;
  state.scripts = scripts; state.queues = queues; state.plugins = status.plugins || [];
  render(pageHeader(t("ui.scheduler"), t("ui.scheduler"), t("ui.start_tasks_manually_watch_live_output_and_cancel_runs_when_needed")) + pluginSlotMarkup("dispatch.cards", "dispatch.cards") + `
    <div id="system-action-area"></div>
    <section class="content-section list-surface" id="dispatch-running" data-testid="dispatch-running"><div class="section-heading"><h3>${t("ui.running")} (${(status.running || []).length})</h3><span class="muted">${t("ui.updates_every_second")}</span></div><div id="running-list">${runningMarkup(status.running || [])}</div></section>${pluginSlotMarkup("dispatch.running.badges", "dispatch.running.badges")}
    <section class="content-section" aria-labelledby="dispatch-run-heading"><div class="section-heading"><h3 id="dispatch-run-heading">${t("ui.start_one_run")}</h3><span class="muted">${t("ui.select_a_target_to_add_it_to_the_run_list")}</span></div>
      <div class="dispatch-runbar">
        ${selectField("dc-kind", t("ui.target_type"), "script", [{ value: "script", label: t("ui.script_instance") }, { value: "queue", label: t("ui.schedule_queues") }], 'data-action="dispatch-kind"')}
        <div class="field" id="dc-script-wrap"><label class="field-label" for="dc-script-trigger">${t("ui.script_instance")}</label>${selectControlMarkup("dc-script", "", [{ value: "", label: t("ui.select_a_script_instance_fc5e40d7") }, ...scripts.map(dispatchScriptOption)], 'data-testid="dispatch-script"', t("ui.script_instance"))}</div>
        <div class="field" id="dc-queue-wrap" hidden><label class="field-label" for="dc-queue-trigger">${t("ui.schedule_queues")}</label>${selectControlMarkup("dc-queue", "", [{ value: "", label: t("ui.select_a_queue_1de509b4") }, ...queues.map(queue => ({ value: queue.id, label: queue.name }))], "", t("ui.schedule_queues"))}</div>
        <div class="control-action"><button id="dc-explain" class="ghost" type="button" data-action="explain-current" data-testid="dispatch-explain">${t("ui.check_run_plan")}</button><button id="dc-run" class="primary" type="button" data-action="dispatch-current" data-testid="dispatch-run">${t("ui.run_script")}</button></div>
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
  catch (error) { if (isCurrent("dispatch", token)) toast(t("ui.status_update_failed") + error.message, "error"); }
  schedule(() => refreshDispatch(token), 1000, "dispatch", token);
}

export async function dispatchScript() {
  const id = $("#dc-script")?.value;
  if (!id) { toast(t("ui.select_a_script_instance"), "error"); return; }
  const script = (state.scripts || []).find(item => item.id === id);
  const unavailableMessage = script ? scriptPluginUnavailableMessage(script, state.plugins || []) : "";
  if (unavailableMessage) { toast(unavailableMessage, "error"); return; }
  try { await api("POST", "/api/dispatch/script", { scriptId: id, mode: "manual" }); toast(t("ui.run_started")); }
  catch (error) { toast(error.message, "error"); }
}

export async function dispatchQueue() {
  const id = $("#dc-queue")?.value;
  if (!id) { toast(t("ui.select_a_queue"), "error"); return; }
  try { await api("POST", "/api/dispatch/queue", { queueId: id, mode: "manual" }); toast(t("ui.run_started")); }
  catch (error) { toast(error.message, "error"); }
}

export function dispatchKindChange(target) {
  const kind = target.value === "queue" ? "queue" : "script";
  const scriptWrap = $("#dc-script-wrap");
  const queueWrap = $("#dc-queue-wrap");
  const runButton = $("#dc-run");
  if (scriptWrap) scriptWrap.hidden = kind !== "script";
  if (queueWrap) queueWrap.hidden = kind !== "queue";
  if (runButton) runButton.textContent = t(kind === "queue" ? "ui.run_queue" : "ui.run_script");
}

export async function dispatchCurrent() {
  const kind = $("#dc-kind")?.value || "script";
  if (kind === "queue") return dispatchQueue();
  return dispatchScript();
}

function explainUserStatus(user) {
  const status = user?.status || "ready";
  const label = status === "ready" ? t("ui.ready") : status === "skipped" ? t("ui.will_skip") : t("ui.blocked");
  const css = status === "ready" ? "ok" : status === "skipped" ? "blue" : "bad";
  const today = Number.isInteger(user?.successfulRunsToday)
    ? t("ui.successful_today_value_value", { successful: user.successfulRunsToday, maximum: user.maxSuccessfulRunsPerDay > 0 ? user.maxSuccessfulRunsPerDay : t("ui.unlimited") })
    : t("ui.not_tracked");
  const reason = t(`dispatch.reason.${user?.reasonCode || "ready"}`, user?.reasonArgs || {}, user?.reasonCode || t("ui.ready"));
  return `<div class="execution-plan-row execution-plan-user-row" role="row">
    <div class="execution-plan-cell execution-plan-name" role="cell">${esc(user?.userName || t("ui.unnamed_user"))}</div>
    <div class="execution-plan-cell execution-plan-status" role="cell"><span class="badge ${css}">${esc(label)}</span></div>
    <div class="execution-plan-cell execution-plan-today muted" role="cell">${esc(today)}</div>
    <div class="execution-plan-cell execution-plan-reason muted" role="cell">${esc(reason)}</div>
  </div>`;
}

function explainQueueClassLabel(queueClass) {
  const normalized = String(queueClass || "").toLowerCase().replaceAll("-", "_");
  const key = normalized === "emulatoronly" ? "ui.queue_class_emulator_only" : normalized === "emulator_only" ? "ui.queue_class_emulator_only" : normalized === "standard" ? "ui.queue_class_standard" : "";
  return key ? t(key) : String(queueClass || t("ui.not_tracked"));
}

function explainCompletionActionLabel(action) {
  const labels = {
    none: "ui.no_action",
    exit: "ui.exit_application",
    sleep: "ui.sleep",
    reboot: "ui.restart",
    shutdown: "ui.shut_down",
  };
  const key = labels[String(action || "none").toLowerCase()];
  return key ? t(key) : String(action || t("ui.no_action"));
}

function explainTaskRow(task, index) {
  const name = task.scriptName || task.taskId || "";
  return `<div class="execution-plan-row execution-plan-task-row" role="row">
    <div class="execution-plan-cell execution-plan-name" role="cell"><div class="execution-plan-task-main"><span class="execution-plan-task-index" aria-hidden="true">${index + 1}</span><span class="execution-plan-task-copy"><strong>${esc(name)}</strong></span></div></div>
    <div class="execution-plan-cell execution-plan-users" role="cell"><span class="badge blue">${esc(t("ui.value_users", { count: task.userCount ?? 0 }))}</span></div>
  </div>`;
}

function explainPlanMarkup(result) {
  const failure = result?.admissionFailure;
  const status = result?.admissible ? t("ui.ready_to_run") : t("ui.cannot_start_now");
  const statusClass = result?.admissible ? "ok" : "bad";
  const tasks = Array.isArray(result?.tasks) ? result.tasks : [];
  const users = Array.isArray(result?.users) ? result.users : [];
  const warnings = Array.isArray(result?.warnings) ? result.warnings : [];
  const totalTasks = Number.isFinite(Number(result?.totalTasks)) ? Number(result.totalTasks) : tasks.length;
  const queueClass = explainQueueClassLabel(result?.queueClass);
  const completionAction = explainCompletionActionLabel(result?.completionAction);
  return `<div class="execution-plan" data-testid="execution-explain-result" role="table" aria-label="${esc(t("ui.run_plan_check"))}">
    <div class="execution-plan-summary">
      <div class="execution-plan-summary-main"><span class="execution-plan-summary-label">${esc(t("ui.target"))}</span><strong>${esc(result?.targetName || "")}</strong><span class="badge ${statusClass}">${esc(status)}</span></div>
      <div class="execution-plan-summary-meta">
        <div class="execution-plan-stat"><span class="k">${esc(t("ui.task"))}</span><strong class="execution-plan-stat-value">${esc(t("ui.value_tasks", { count: totalTasks }))}</strong><span class="muted execution-plan-stat-subvalue">${esc(queueClass)}</span></div>
        <div class="execution-plan-stat"><span class="k">${esc(t("ui.completion_action"))}</span><strong class="execution-plan-stat-value">${esc(completionAction)}</strong></div>
      </div>
    </div>
    ${failure ? `<div class="callout callout-warning execution-plan-warning"><strong>${esc(t(`api.error.${failure.code || "admission_failed"}`, failure.args || {}, failure.code || t("ui.unable_to_start")))}</strong></div>` : ""}
    ${tasks.length ? `<section class="execution-plan-section" aria-labelledby="execution-explain-tasks-heading"><div class="execution-plan-section-heading"><div class="execution-plan-section-heading-main"><h4 id="execution-explain-tasks-heading">${esc(t("ui.task_list"))}</h4><span class="badge muted">${esc(t("ui.value_tasks", { count: tasks.length }))}</span></div></div><div class="execution-plan-table-header execution-plan-task-header" role="row"><span role="columnheader">${esc(t("ui.task"))}</span><span role="columnheader">${esc(t("ui.users"))}</span></div>${tasks.map((task, index) => explainTaskRow(task, index)).join("")}</section>` : ""}
    ${users.length ? `<section class="execution-plan-section" aria-labelledby="execution-explain-users-heading"><div class="execution-plan-section-heading"><div class="execution-plan-section-heading-main"><h4 id="execution-explain-users-heading">${esc(t("ui.user_eligibility"))}</h4><span class="badge muted">${esc(t("ui.value_users", { count: users.length }))}</span></div></div><div class="execution-plan-table-header execution-plan-user-header" role="row"><span role="columnheader">${esc(t("ui.user"))}</span><span role="columnheader">${esc(t("ui.status"))}</span><span role="columnheader">${esc(t("ui.successful_today"))}</span><span role="columnheader">${esc(t("ui.reason"))}</span></div>${users.map(explainUserStatus).join("")}</section>` : ""}
    ${warnings.length ? `<div class="callout callout-warning execution-plan-warning"><strong>${esc(t("ui.notice"))}</strong><br>${warnings.map(item => esc(t(`dispatch.warning.${item.code}`, item.args || {}, item.code || t("ui.needs_attention")))).join("<br>")}</div>` : ""}
  </div>`;
}

export async function explainCurrent() {
  const kind = $("#dc-kind")?.value === "queue" ? "queue" : "script";
  const id = kind === "queue" ? $("#dc-queue")?.value : $("#dc-script")?.value;
  if (!id) { toast(t(kind === "queue" ? "ui.select_a_queue" : "ui.select_a_script_instance"), "error"); return; }
  try {
    const result = await api("POST", `/api/dispatch/explain/${kind}`, kind === "queue" ? { queueId: id } : { scriptId: id });
    showModal(modalShell(t("ui.run_plan_check"), explainPlanMarkup(result?.result || result), `<button class="ghost" type="button" data-action="close-modal">${t("ui.close")}</button>`), true);
  } catch (error) { toast(error.message, "error"); }
}

export function cancelRun(runId) {
  confirmModal(t("ui.cancel_run"), t("ui.the_current_task_will_be_terminated_if_this_is_a_queue_subsequent_tasks_will_not_run_cancel_it"), "confirm-cancel-run", { id: runId });
}

export async function confirmCancelRun(runId) {
  try { await api("POST", "/api/cancel", { runId }); closeModal(); toast(t("ui.cancellation_requested")); }
  catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "dispatch-script": target => withBusy(target, () => dispatchScript()),
  "dispatch-queue": target => withBusy(target, () => dispatchQueue()),
  "dispatch-current": target => withBusy(target, () => dispatchCurrent()),
  "explain-current": target => withBusy(target, () => explainCurrent()),
  "dispatch-kind": target => dispatchKindChange(target),
  "cancel-run": target => cancelRun(target.dataset.id),
  "confirm-cancel-run": target => withBusy(target, () => confirmCancelRun(target.dataset.id)),
};
