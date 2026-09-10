import { api, hydrateIcons } from "../../core/api.js";
import { $, $$ } from "../../core/dom.js";
import { esc, scriptPluginStatus, scriptPluginUnavailableMessage } from "../../core/format.js";
import { pageHeader, valueField } from "../../core/forms.js";
import { icon } from "../../core/icons.js";
import { isCurrent, registerInterval, schedule, state } from "../../core/state.js";
import { closeModal, modalShell, showModal } from "../../core/modal.js";
import { hasEntityNameConflict } from "../../core/entity-name.js";
import { navActive, render, setFieldError, setFieldInvalid, setRequiredFieldError, clearFieldError, setTopbarTitle, toast, pushNotice, withBusy } from "../../core/ui.js";
import { initDndList } from "../../core/dnd.js";
import { pluginSlotMarkup, renderPluginSlots } from "../../core/plugin-slots.js";
import { durationClock } from "../../core/duration.js";
import { buildConfigEditRequest } from "./config-edit.js";
import { t } from "../../core/i18n.js";

export const MAX_ENTITY_NAME_BYTES = 64;
export const MAX_USER_REMARK_BYTES = 512;

let managementDraft = null;
let deleteDraft = null;
let userListBadgesByUser = new Map();
let nextTimer = null;
let nextRefreshPending = false;

export function getManagementDraft() {
  return managementDraft;
}

export function setManagementDraft(value) {
  managementDraft = value;
}

export function userById(id) {
  return (state.users || []).find(user => user.id === id);
}

export function cloneUser(user) {
  return JSON.parse(JSON.stringify(user));
}

export function scriptById(id) {
  return (state.scripts || []).find(script => script.id === id);
}

export function unavailableScriptMessage(scriptId) {
  const script = scriptById(scriptId);
  return script ? scriptPluginUnavailableMessage(script, state.plugins || []) : "";
}

function initials(name) {
  const chars = Array.from((name || t("common.user")).trim());
  if (!chars.length) return t("users.user_initial");
  const first = chars[0];
  return /[\u3400-\u9fff]/.test(first) ? first : chars.slice(0, 2).join("").toUpperCase();
}

function nextRunLabel(value) {
  return value ? t("common.calculating_countdown") : t("users.no_scheduled_tasks");
}

function remainingLabel(milliseconds) {
  if (milliseconds <= 0) return t("common.about_to_run");
  const seconds = Math.floor(milliseconds / 1000);
  const days = Math.floor(seconds / 86400);
  const clock = durationClock(days > 0 ? seconds % 86400 : seconds);
  return days ? t("users.schedule.runs_in_days", { days, clock }) : t("users.schedule.runs_in", { clock });
}

function tickUserCountdowns() {
  const now = Date.now();
  let shouldRefresh = false;
  $$("#view .global-user-next-run[data-next-run]").forEach(element => {
    const raw = element.dataset.nextRun || "";
    if (!raw) {
      element.textContent = t("users.no_scheduled_tasks");
      return;
    }
    const target = new Date(raw).getTime();
    if (!Number.isFinite(target)) {
      element.textContent = t("users.no_scheduled_tasks");
      return;
    }
    const remaining = target - now;
    element.textContent = remainingLabel(remaining);
    if (remaining <= 0 && element.dataset.refreshRequested !== "true") {
      element.dataset.refreshRequested = "true";
      shouldRefresh = true;
    }
  });
  if (shouldRefresh && !nextRefreshPending) {
    nextRefreshPending = true;
    schedule(async () => {
      nextRefreshPending = false;
      if (state.page === "users") await reloadUsers();
    }, 1500, "users", state.routeToken);
  }
}

function avatarMarkup(user) {
  const content = user.avatarUrl
    ? '<img class="global-user-avatar" src="' + esc(user.avatarUrl) + '" alt="" loading="lazy">'
    : '<span class="global-user-avatar global-user-avatar-fallback" aria-hidden="true">' + esc(initials(user.name)) + "</span>";
  return '<button class="global-user-avatar-button" type="button" data-action="upload-user-avatar" data-user-id="' + esc(user.id) + '" aria-label="' + esc(t("users.avatar_upload_for_user", { name: user.name })) + '" title="' + esc(t("users.avatar_upload")) + '">' +
    content + '<span class="global-user-avatar-mark" aria-hidden="true">+</span></button>';
}

function pluginUserBadgeMarkup(user) {
  const badges = userListBadgesByUser.get(user.id);
  if (!Array.isArray(badges)) return "";
  const allowedTones = new Set(["muted", "blue", "ok", "warn", "bad"]);
  return badges.map(badge => {
    const label = String(badge?.label ?? "").trim();
    if (!label) return "";
    const tone = allowedTones.has(String(badge?.tone || "").toLowerCase())
      ? String(badge.tone).toLowerCase()
      : "muted";
    return '<span class="badge ' + tone + '" data-testid="plugin-user-badge" data-plugin-name="' + esc(badge?.pluginName) + '" data-contribution-id="' + esc(badge?.id) + '" title="' + esc(badge?.title) + '">' + esc(label) + "</span>";
  }).join("");
}

function userCard(user) {
  const bindingCount = user.bindingCount ?? (user.bindings || []).length;
  const nextRun = user.nextRunAt || "";
  const queueTitle = user.nextQueueName ? t("users.next_queue_value", { name: user.nextQueueName }) : "";
  return '<article class="script-card global-user-card" data-dnd-id="' + esc(user.id) + '" data-testid="global-user-card">' +
    '<span class="drag-handle" role="button" tabindex="0" aria-label="' + esc(t("users.global.order_help")) + '" title="' + esc(t("common.drag_to_reorder")) + '">' + icon("grip") + "</span>" +
    avatarMarkup(user) +
    '<div class="script-main global-user-main">' +
      '<div class="script-name-row"><strong class="global-user-name">' + esc(user.name) + "</strong></div>" +
      '<div class="meta-line global-user-meta">' +
        '<span class="badge muted">' + t("users.binding.scripts_count", { count: bindingCount }) + "</span>" +
        pluginUserBadgeMarkup(user) +
        pluginSlotMarkup("users.list.badges", "user-" + user.id, "user-plugin-slot", { mode: "list", primaryId: user.id }) +
        '<span class="badge blue global-user-next-run" data-next-run="' + esc(nextRun) + '" title="' + esc(queueTitle) + '">' + esc(nextRunLabel(nextRun)) + "</span>" +
      "</div>" +
    "</div>" +
    '<div class="global-user-actions row-actions entity-actions">' +
      '<button class="tertiary" type="button" data-action="open-user-management" data-user-id="' + esc(user.id) + '" aria-label="' + esc(t("users.user_management")) + '" title="' + esc(t("users.user_management")) + '">' + t("users.user_management_button") + "</button>" +
      '<button class="tertiary" type="button" data-action="open-global-management" data-user-id="' + esc(user.id) + '" aria-label="' + esc(t("users.global.title")) + '" title="' + esc(t("users.global.title")) + '">' + t("users.global.open_action") + "</button>" +
      '<button class="danger" type="button" data-action="delete-global-user" data-user-id="' + esc(user.id) + `">${t("users.delete_user")}</button>` +
    "</div>" +
  "</article>";
}

export async function pageUsers(token) {
  if (!isCurrent("users", token)) return;
  navActive("users");
  setTopbarTitle(t("users.user_management"));
  nextRefreshPending = false;
  if (nextTimer) {
    clearInterval(nextTimer);
    nextTimer = null;
  }
  let users, scripts, status, userListBadges;
  try {
    [users, scripts, status, userListBadges] = await Promise.all([
      api("GET", "/api/users"),
      api("GET", "/api/scripts"),
      api("GET", "/api/status"),
      api("GET", "/api/plugin-contributions/user-list-badges"),
    ]);
  } catch (error) {
    if (isCurrent("users", token)) render('<div class="empty"><strong>' + t("users.error.load") + '</strong><span>' + esc(error.message) + "</span></div>");
    return;
  }
  if (!isCurrent("users", token)) return;
  state.scripts = scripts;
  state.plugins = status.plugins || [];
  state.users = users || [];
  userListBadgesByUser = new Map((Array.isArray(userListBadges) ? userListBadges : []).map(item => [item?.userId, Array.isArray(item?.badges) ? item.badges : []]));
  const limit = state.limits?.maxUsers ?? 50;
  const atLimit = state.users.length >= limit;
  const action = '<button class="primary" type="button" data-action="open-global-user-modal" data-testid="open-global-user-modal" ' + (atLimit ? "disabled" : "") + `>${atLimit ? t("users.action.add_count", { current: state.users.length, maximum: limit }) : t("users.add_user")}</button>`;
  const sorted = state.users.slice().sort((a, b) => (a.index ?? 0) - (b.index ?? 0));
  const content = sorted.length
    ? '<section class="card list-surface"><div class="script-grid global-user-list" id="global-user-list">' + sorted.map(userCard).join("") + "</div></section>"
    : `<div class="empty"><strong>${t("users.no_users_yet")}</strong><span>${t("users.page.empty_help")}</span></div>`;
  render(pageHeader(t("users.account_management"), t("users.user_management"), t("users.page.help"), action) + content);
  await renderPluginSlots(document.querySelector("#view"));
  const list = $("#global-user-list");
  if (list) initDndList(list, { onDrop: reorderGlobalUsers });
  hydrateIcons($("#view"));
  tickUserCountdowns();
  if (sorted.some(user => user.nextRunAt)) {
    nextTimer = setInterval(tickUserCountdowns, 1000);
    registerInterval(nextTimer);
  }
  await restoreEditSessionCard();
}

export async function reloadUsers() {
  await pageUsers(state.routeToken);
}

/** 刷新或服务重启后恢复仍在进行的配置编辑锁定弹窗。 */
async function restoreEditSessionCard() {
  if ($(".modal-mask")) return;
  try {
    const sessions = await api("GET", "/api/scripts/edit-sessions");
    const session = (sessions || []).find(item => {
      const user = (state.users || []).find(candidate =>
        candidate.id === item.userId || candidate.name === item.userName);
      return user && (user.bindings || []).some(binding => binding.scriptInstanceId === item.scriptId);
    });
    if (!session || $(".modal-mask")) return;
    const user = (state.users || []).find(candidate =>
      candidate.id === session.userId || candidate.name === session.userName);
    const binding = user?.bindings?.find(item => item.scriptInstanceId === session.scriptId);
    const script = scriptById(session.scriptId);
    if (user && binding && script) showGlobalEditConfigCard(user.id, script.id, user.name, script.name, session.editMode);
  } catch {
    // 编辑会话恢复失败时保留页面，用户可从绑定卡片重新进入配置编辑。
  }
}

export function openGlobalUserModal() {
  const body = valueField("gu-name", `${t("users.user_name")} <span class='req'>*</span>`, "", "text", `placeholder="${t("users.editor.name.placeholder")}"`, t("users.username_case_insensitive"));
  showModal(modalShell(t("users.add_user"), body, `<button class="primary" type="button" data-action="save-global-user" data-testid="save-global-user">${t("common.save")}</button><button class="ghost" type="button" data-action="close-modal">${t("common.cancel")}</button>`), false, true, true);
}

export async function saveGlobalUser() {
  const name = $("#gu-name")?.value.trim() || "";
  if (!name) {
    setRequiredFieldError("gu-name");
    toast(t("users.validation.username_required"), "error");
    return;
  }
  if (new TextEncoder().encode(name).length > MAX_ENTITY_NAME_BYTES) {
    setFieldError("gu-name", t("users.username_max_bytes", { bytes: MAX_ENTITY_NAME_BYTES }));
    toast(t("users.validation.username_length", { bytes: MAX_ENTITY_NAME_BYTES }), "error");
    return;
  }
  if (hasEntityNameConflict(state.users, name)) {
    setFieldInvalid("gu-name");
    toast(t("users.validation.username_duplicate"), "error");
    return;
  }
  clearFieldError("gu-name");
  try {
    await api("POST", "/api/users", { name });
    closeModal();
    toast(t("users.user_created"));
    await reloadUsers();
  } catch (error) {
    if (error?.code === "duplicate_name") {
      setFieldInvalid("gu-name");
      toast(t("users.validation.username_duplicate"), "error");
      return;
    }
    toast(error.message, "error");
  }
}

export function availableScripts(user) {
  const boundIds = new Set((user.bindings || []).map(binding => binding.scriptInstanceId));
  return (state.scripts || [])
    .slice()
    .sort((a, b) => (a.index ?? 0) - (b.index ?? 0))
    .filter(script => !boundIds.has(script.id))
    .filter(script => {
      const status = scriptPluginStatus(script, state.plugins || []);
      return !status.specialized || status.available;
    });
}

export function deleteGlobalUser(id) {
  const user = userById(id);
  if (!user) return;
  deleteDraft = user;
  const body = '<p class="modal-copy">' + t("users.confirm.delete", { name: esc(user.name) }) + '</p>' +
    valueField("gu-delete-name", `${t("users.confirm_username")} <span class='req'>*</span>`, "", "text", 'placeholder="' + esc(user.name) + '"');
  showModal(modalShell(t("users.delete_user"), body, '<button class="danger solid" type="button" data-action="confirm-delete-global-user" data-testid="confirm-delete-global-user">' + t("common.confirm_deletion") + '</button><button class="ghost" type="button" data-action="close-modal">' + t("common.cancel") + '</button>'));
}

export async function confirmDeleteGlobalUser() {
  if (!deleteDraft) return;
  const input = $("#gu-delete-name")?.value || "";
  if (!input.trim()) {
    setRequiredFieldError("gu-delete-name");
    toast(t("users.confirm.username_help"), "error");
    return;
  }
  if (input !== deleteDraft.name) {
    setFieldError("gu-delete-name", t("users.validation.username_confirmation"));
    toast(t("users.confirm.username_help"), "error");
    return;
  }
  try {
    await api("DELETE", "/api/users/" + encodeURIComponent(deleteDraft.id), { confirmName: input });
    const deletedName = deleteDraft.name;
    deleteDraft = null;
    closeModal();
    toast(t("users.deleted_user_value", { name: deletedName }));
    await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
  }
}

export async function reorderGlobalUsers(ids) {
  try {
    await api("PUT", "/api/users/order", { ids });
    toast(t("users.user_order_saved"));
    await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
    await reloadUsers();
  }
}

function showGlobalEditConfigCard(userId, scriptId, userName, scriptName, editMode) {
  const mode = editMode || "normal";
  const copy = mode === "fresh"
    ? t("users.config.edit_new_help")
    : mode === "reuse"
      ? t("users.config.edit_existing_help")
      : t("users.config.edit_manual_help", { user: esc(userName), script: esc(scriptName) });
  showModal(modalShell(t("users.config.edit_progress"), '<p class="modal-copy">' + copy + '</p>',
    '<button class="primary" type="button" data-action="global-edit-config-done" data-user-id="' + esc(userId) + '" data-script-id="' + esc(scriptId) + '" data-mode="' + esc(mode) + '">' + t("common.complete") + '</button><button class="ghost" type="button" data-action="global-edit-config-cancel" data-user-id="' + esc(userId) + '" data-script-id="' + esc(scriptId) + '" data-mode="' + esc(mode) + '">' + t("common.cancel") + '</button>'), false, true);
}

export async function editGlobalUserConfig(userId, scriptId) {
  const user = userById(userId);
  const binding = user?.bindings?.find(item => item.scriptInstanceId === scriptId);
  if (!user || !binding) return;
  const unavailableMessage = unavailableScriptMessage(scriptId);
  if (unavailableMessage) {
    toast(unavailableMessage, "error");
    return;
  }
  try {
    const status = await api("GET", "/api/users/" + encodeURIComponent(userId) + "/bindings/" + encodeURIComponent(scriptId) + "/edit-config");
    if (status && status.hasSnapshot) {
      await startEditConfig(userId, scriptId, "normal");
      return;
    }
    // 首次编辑（无配置快照）：选择全新生成或复用现有配置
    openFirstEditConfigChooser(userId, scriptId);
  } catch (error) {
    toast(error.message, "error");
  }
}

/** 专项插件是否声明脚本没有生成全新配置文件的能力（no-fresh-config）。 */
function pluginLacksFreshConfig(scriptId) {
  const script = (state.scripts || []).find(item => item.id === scriptId);
  const meta = (state.plugins || []).find(item => item.name === script?.pluginType);
  return !!meta?.noFreshConfig;
}

function openFirstEditConfigChooser(userId, scriptId) {
  const freshDisabled = pluginLacksFreshConfig(scriptId);
  const freshCard = freshDisabled
    ? '<button type="button" class="chooser-card" disabled>' +
      `<strong>${t("users.fresh_configuration_file")}</strong><span class="muted">${t("users.config.unavailable")}</span></button>`
    : '<button type="button" class="chooser-card" data-action="first-edit-config-fresh" data-user-id="' + esc(userId) + '" data-script-id="' + esc(scriptId) + '">' +
      `<strong>${t("users.fresh_configuration_file")}</strong><span class="muted">${t("users.config.generated")}</span></button>`;
  const body = `<p class="modal-copy">${t("users.config.edit_first", { script: t("users.this_script_instance") })}</p>` +
    '<div class="first-edit-chooser">' +
    freshCard +
    '<button type="button" class="chooser-card" data-action="first-edit-config-reuse" data-user-id="' + esc(userId) + '" data-script-id="' + esc(scriptId) + '">' +
    `<strong>${t("users.reuse_configuration_file")}</strong><span class="muted">${t("users.config.edit_existing")}</span></button>` +
    '</div>';
  const footer = `<button class="ghost" type="button" data-action="close-modal">${t("common.cancel")}</button>`;
  showModal(modalShell(`${t("users.first_edit")} ${t("users.edit_configuration")}`, body, footer), false, true, true);
}

function createRequesterWindowToken() {
  const cryptoApi = globalThis.crypto;
  if (cryptoApi && typeof cryptoApi.getRandomValues === "function") {
    const bytes = new Uint8Array(8);
    cryptoApi.getRandomValues(bytes);
    return Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
  }
  return Math.random().toString(36).slice(2, 18).padEnd(16, "0");
}

function waitForRequesterTitlePaint() {
  return new Promise(resolve => {
    if (typeof requestAnimationFrame === "function") {
      requestAnimationFrame(() => resolve());
      return;
    }
    setTimeout(resolve, 0);
  });
}

async function startEditConfig(userId, scriptId, mode, inputOverride = null) {
  const requesterWindowToken = createRequesterWindowToken();
  const previousTitle = document.title;
  document.title = t("users.nexuspipeline_core") + " · " + requesterWindowToken;
  try {
    await waitForRequesterTitlePaint();
    const request = buildConfigEditRequest(mode, inputOverride, requesterWindowToken);
    await api("POST", "/api/users/" + encodeURIComponent(userId) + "/bindings/" + encodeURIComponent(scriptId) + "/edit-config", request);
  } catch (error) {
    if (error.code === "config_input_mismatch" && Array.isArray(error.data?.candidates) && error.data.candidates.length > 0) {
      const inputName = String(error.data.inputName || "");
      if (!inputName) {
        toast(t("users.config.input_missing"), "error");
        return;
      }
      openConfigCandidateChooser(userId, scriptId, mode, error.data.candidates, inputName);
      return;
    }
    throw error;
  } finally {
    document.title = previousTitle;
  }
  const user = userById(userId);
  const binding = user?.bindings?.find(item => item.scriptInstanceId === scriptId);
  showGlobalEditConfigCard(userId, scriptId, user?.name || "", binding?.scriptName || t("common.script_instance"), mode);
}

/** 复用编辑候选选择：现场存在多个配置时，把候选作为编辑会话临时输入；成功保存后才写入用户绑定。 */
function openConfigCandidateChooser(userId, scriptId, mode, candidates, inputName) {
  const cards = candidates.map(candidate =>
    '<button type="button" class="chooser-card" data-action="adopt-config-candidate" data-user-id="' + esc(userId) + '" data-script-id="' + esc(scriptId) + '" data-mode="' + esc(mode) + '" data-candidate="' + esc(candidate) + '" data-input-name="' + esc(inputName) + '">' +
    '<strong class="scroll-text"><span class="scroll-inner">' + esc(candidate) + `</span></strong><span class="muted">${t("users.config.candidate_used")}</span></button>`).join("");
  const body = `<p class="modal-copy">${t("users.config.candidates_help")}</p>` +
    '<div class="first-edit-chooser">' + cards + '</div>';
  const footer = `<button class="ghost" type="button" data-action="close-modal">${t("common.cancel")}</button>`;
  showModal(modalShell(t("users.take_over_configuration"), body, footer), false, true, true);
}

async function adoptConfigCandidate(target) {
  const userId = target.dataset.userId;
  const scriptId = target.dataset.scriptId;
  const mode = target.dataset.mode;
  const candidate = target.dataset.candidate;
  const inputName = target.dataset.inputName || "";
  try {
    if (!inputName) {
      toast(t("users.config.input_name_missing"), "error");
      return;
    }
    closeModal();
    toast(t("users.config.candidate_label", { candidate }));
    await startEditConfig(userId, scriptId, mode, { name: inputName, value: candidate });
  } catch (error) {
    if (error.code === "config_input_mismatch" && Array.isArray(error.data?.candidates) && error.data.candidates.length > 0) {
      const nextInputName = String(error.data.inputName || inputName || "");
      if (!nextInputName) {
        toast(t("users.config.input_missing"), "error");
        return;
      }
      openConfigCandidateChooser(userId, scriptId, mode, error.data.candidates, nextInputName);
      return;
    }
    toast(error.message, "error");
  }
}

export async function chooseFirstEditConfigMode(target) {
  const mode = target.dataset.action === "first-edit-config-fresh" ? "fresh" : "reuse";
  const userId = target.dataset.userId;
  const scriptId = target.dataset.scriptId;
  try {
    closeModal();
    await startEditConfig(userId, scriptId, mode);
  } catch (error) {
    toast(error.message, "error");
  }
}

export async function globalEditConfigAction(userId, scriptId, action, mode) {
  try {
    const result = await api("POST", "/api/users/" + encodeURIComponent(userId) + "/bindings/" + encodeURIComponent(scriptId) + "/edit-config", { action });
    setManagementDraft(null);
    closeModal();
    let message;
    if (action === "done") {
      message = mode === "fresh" ? t("users.config.snapshot_saved") : t("users.user_configuration_saved", { user: t("common.user") });
    } else {
      message = mode === "reuse" ? t("common.cancelled") : t("users.config.cancelled_restored");
    }
    toast(message);
    if (action === "done") {
      const validation = result?.validation;
      for (const item of Array.isArray(validation?.toasts) ? validation.toasts : []) {
        toast(item?.message || "", item?.kind || "info");
      }
      for (const item of Array.isArray(validation?.notifications) ? validation.notifications : []) {
        pushNotice(item?.title || t("users.config.check_fallback"), item?.body || "", item?.kind || "info");
      }
      if (validation?.error) {
        toast(t("users.error.plugin_validation"), "error");
      }
    }
    await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
  }
}

/** 开关视觉状态同步：用户管理与全局管理共用。 */
export function syncManagementSwitch(target, pressed) {
  if (!target) return;
  target.setAttribute("aria-pressed", pressed ? "true" : "false");
  target.dataset.state = pressed ? "on" : "off";
  const stateText = target.querySelector("[data-switch-state]");
  if (stateText) stateText.textContent = pressed ? t("common.enabled") : t("common.disabled");
}

export function toggleManagementSwitch(target) {
  if (target.disabled) return;
  const on = target.getAttribute("aria-pressed") === "true";
  syncManagementSwitch(target, !on);
}

export const actions = {
  "open-global-user-modal": () => openGlobalUserModal(),
  "save-global-user": target => withBusy(target, () => saveGlobalUser()),
  "delete-global-user": target => deleteGlobalUser(target.dataset.userId),
  "confirm-delete-global-user": target => withBusy(target, () => confirmDeleteGlobalUser()),
  "edit-user-config-global": target => editGlobalUserConfig(target.dataset.userId, target.dataset.scriptId),
  "global-edit-config-done": target => withBusy(target, () => globalEditConfigAction(target.dataset.userId, target.dataset.scriptId, "done", target.dataset.mode)),
  "global-edit-config-cancel": target => withBusy(target, () => globalEditConfigAction(target.dataset.userId, target.dataset.scriptId, "cancel", target.dataset.mode)),
  "first-edit-config-fresh": target => chooseFirstEditConfigMode(target),
  "first-edit-config-reuse": target => chooseFirstEditConfigMode(target),
  "adopt-config-candidate": target => withBusy(target, () => adoptConfigCandidate(target)),
};
