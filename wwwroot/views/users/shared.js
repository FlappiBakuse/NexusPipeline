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
  const chars = Array.from((name || t("ui.user")).trim());
  if (!chars.length) return t("ui.user_initial");
  const first = chars[0];
  return /[\u3400-\u9fff]/.test(first) ? first : chars.slice(0, 2).join("").toUpperCase();
}

function nextRunLabel(value) {
  return value ? t("ui.calculating_countdown") : t("ui.no_scheduled_tasks");
}

function remainingLabel(milliseconds) {
  if (milliseconds <= 0) return t("ui.about_to_run");
  const seconds = Math.floor(milliseconds / 1000);
  const days = Math.floor(seconds / 86400);
  const clock = durationClock(days > 0 ? seconds % 86400 : seconds);
  return days ? t("ui.runs_in_valued_value", { days, clock }) : t("ui.runs_in_value", { clock });
}

function tickUserCountdowns() {
  const now = Date.now();
  let shouldRefresh = false;
  $$("#view .global-user-next-run[data-next-run]").forEach(element => {
    const raw = element.dataset.nextRun || "";
    if (!raw) {
      element.textContent = t("ui.no_scheduled_tasks");
      return;
    }
    const target = new Date(raw).getTime();
    if (!Number.isFinite(target)) {
      element.textContent = t("ui.no_scheduled_tasks");
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
  return '<button class="global-user-avatar-button" type="button" data-action="upload-user-avatar" data-user-id="' + esc(user.id) + '" aria-label="' + esc(t("ui.avatar_upload_for_user", { name: user.name })) + '" title="' + esc(t("ui.avatar_upload")) + '">' +
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
  const queueTitle = user.nextQueueName ? t("ui.next_queue_value", { name: user.nextQueueName }) : "";
  return '<article class="script-card global-user-card" data-dnd-id="' + esc(user.id) + '" data-testid="global-user-card">' +
    '<span class="drag-handle" role="button" tabindex="0" aria-label="' + esc(t("ui.drag_to_reorder_global_users")) + '" title="' + esc(t("ui.drag_to_reorder")) + '">' + icon("grip") + "</span>" +
    avatarMarkup(user) +
    '<div class="script-main global-user-main">' +
      '<div class="script-name-row"><strong class="global-user-name">' + esc(user.name) + "</strong></div>" +
      '<div class="meta-line global-user-meta">' +
        '<span class="badge muted">' + t("ui.value_scripts_bound", { count: bindingCount }) + "</span>" +
        pluginUserBadgeMarkup(user) +
        pluginSlotMarkup("users.list.badges", "user-" + user.id, "user-plugin-slot", { mode: "list", primaryId: user.id }) +
        '<span class="badge blue global-user-next-run" data-next-run="' + esc(nextRun) + '" title="' + esc(queueTitle) + '">' + esc(nextRunLabel(nextRun)) + "</span>" +
      "</div>" +
    "</div>" +
    '<div class="global-user-actions row-actions entity-actions">' +
      '<button class="tertiary" type="button" data-action="open-user-management" data-user-id="' + esc(user.id) + '" aria-label="' + esc(t("ui.user_management")) + '" title="' + esc(t("ui.user_management")) + '">' + t("ui.user_management_button") + "</button>" +
      '<button class="tertiary" type="button" data-action="open-global-management" data-user-id="' + esc(user.id) + '" aria-label="' + esc(t("ui.global_management")) + '" title="' + esc(t("ui.global_management")) + '">' + t("ui.global_management_button") + "</button>" +
      '<button class="danger" type="button" data-action="delete-global-user" data-user-id="' + esc(user.id) + `">${t("ui.delete_user")}</button>` +
    "</div>" +
  "</article>";
}

export async function pageUsers(token) {
  if (!isCurrent("users", token)) return;
  navActive("users");
  setTopbarTitle(t("ui.user_management"));
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
    if (isCurrent("users", token)) render('<div class="empty"><strong>' + t("ui.failed_to_load_user_management") + '</strong><span>' + esc(error.message) + "</span></div>");
    return;
  }
  if (!isCurrent("users", token)) return;
  state.scripts = scripts;
  state.plugins = status.plugins || [];
  state.users = users || [];
  userListBadgesByUser = new Map((Array.isArray(userListBadges) ? userListBadges : []).map(item => [item?.userId, Array.isArray(item?.badges) ? item.badges : []]));
  const limit = state.limits?.maxUsers ?? 50;
  const atLimit = state.users.length >= limit;
  const action = '<button class="primary" type="button" data-action="open-global-user-modal" data-testid="open-global-user-modal" ' + (atLimit ? "disabled" : "") + `>${t("ui.add_user")}` + (atLimit ? "（" + state.users.length + "/" + limit + "）" : "") + "</button>";
  const sorted = state.users.slice().sort((a, b) => (a.index ?? 0) - (b.index ?? 0));
  const content = sorted.length
    ? '<section class="card list-surface"><div class="script-grid global-user-list" id="global-user-list">' + sorted.map(userCard).join("") + "</div></section>"
    : `<div class="empty"><strong>${t("ui.no_users_yet")}</strong><span>${t("ui.click_add_user_in_the_upper_right_corner_then_bind_one_or_more_script_instances")}</span></div>`;
  render(pageHeader(t("ui.account_management"), t("ui.user_management"), t("ui.manage_user_avatars_script_bindings_run_priority_and_notification_settings"), action) + content);
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
  const body = valueField("gu-name", `${t("ui.user_name")} <span class='req'>*</span>`, "", "text", `placeholder="${t("ui.enter_username_placeholder")}"`, t("ui.username_case_insensitive"));
  showModal(modalShell(t("ui.add_user"), body, `<button class="primary" type="button" data-action="save-global-user" data-testid="save-global-user">${t("ui.save")}</button><button class="ghost" type="button" data-action="close-modal">${t("ui.cancel")}</button>`), false, true, true);
}

export async function saveGlobalUser() {
  const name = $("#gu-name")?.value.trim() || "";
  if (!name) {
    setRequiredFieldError("gu-name");
    toast(t("ui.enter_a_username"), "error");
    return;
  }
  if (new TextEncoder().encode(name).length > MAX_ENTITY_NAME_BYTES) {
    setFieldError("gu-name", t("ui.username_max_bytes", { bytes: MAX_ENTITY_NAME_BYTES }));
    toast(t("ui.usernames_may_contain_at_most_value_bytes", { bytes: MAX_ENTITY_NAME_BYTES }), "error");
    return;
  }
  if (hasEntityNameConflict(state.users, name)) {
    setFieldInvalid("gu-name");
    toast(t("ui.that_username_already_exists_choose_another_name"), "error");
    return;
  }
  clearFieldError("gu-name");
  try {
    await api("POST", "/api/users", { name });
    closeModal();
    toast(t("ui.user_created"));
    await reloadUsers();
  } catch (error) {
    if (error?.code === "duplicate_name") {
      setFieldInvalid("gu-name");
      toast(t("ui.that_username_already_exists_choose_another_name"), "error");
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
  const body = '<p class="modal-copy">' + t("ui.deleting_value_removes_all_script_bindings_and_clears_this_user_s_configuration_enter_the_full_username_to_confirm", { name: esc(user.name) }) + '</p>' +
    valueField("gu-delete-name", `${t("ui.confirm_username")} <span class='req'>*</span>`, "", "text", 'placeholder="' + esc(user.name) + '"');
  showModal(modalShell(t("ui.delete_user"), body, '<button class="danger solid" type="button" data-action="confirm-delete-global-user" data-testid="confirm-delete-global-user">' + t("ui.confirm_deletion") + '</button><button class="ghost" type="button" data-action="close-modal">' + t("ui.cancel") + '</button>'));
}

export async function confirmDeleteGlobalUser() {
  if (!deleteDraft) return;
  const input = $("#gu-delete-name")?.value || "";
  if (!input.trim()) {
    setRequiredFieldError("gu-delete-name");
    toast(t("ui.enter_the_full_username_to_confirm_deletion"), "error");
    return;
  }
  if (input !== deleteDraft.name) {
    setFieldError("gu-delete-name", t("ui.enter_the_username_exactly"));
    toast(t("ui.enter_the_full_username_to_confirm_deletion"), "error");
    return;
  }
  try {
    await api("DELETE", "/api/users/" + encodeURIComponent(deleteDraft.id), { confirmName: input });
    const deletedName = deleteDraft.name;
    deleteDraft = null;
    closeModal();
    toast(t("ui.deleted_user_value", { name: deletedName }));
    await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
  }
}

export async function reorderGlobalUsers(ids) {
  try {
    await api("PUT", "/api/users/order", { ids });
    toast(t("ui.user_order_saved"));
    await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
    await reloadUsers();
  }
}

function showGlobalEditConfigCard(userId, scriptId, userName, scriptName, editMode) {
  const mode = editMode || "normal";
  const copy = mode === "fresh"
    ? t("ui.the_main_program_started_and_the_script_will_create_a_new_configuration_save_the_new_configuration_as_a_snapshot_when_finished_or_cancel_to_restore_the_original")
    : mode === "reuse"
      ? t("ui.the_main_program_started_and_is_editing_the_existing_configuration_save_a_snapshot_when_finished_or_cancel_without_changes")
      : t("ui.the_main_program_started_without_arguments_configure_user_value_in_script_value_then_save_or_cancel_this_edit", { user: esc(userName), script: esc(scriptName) });
  showModal(modalShell(t("ui.configuration_edit_in_progress"), '<p class="modal-copy">' + copy + '</p>',
    '<button class="primary" type="button" data-action="global-edit-config-done" data-user-id="' + esc(userId) + '" data-script-id="' + esc(scriptId) + '" data-mode="' + esc(mode) + '">' + t("ui.complete") + '</button><button class="ghost" type="button" data-action="global-edit-config-cancel" data-user-id="' + esc(userId) + '" data-script-id="' + esc(scriptId) + '" data-mode="' + esc(mode) + '">' + t("ui.cancel") + '</button>'), false, true);
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
      `<strong>${t("ui.fresh_configuration_file")}</strong><span class="muted">${t("ui.fresh_configuration_unavailable")}</span></button>`
    : '<button type="button" class="chooser-card" data-action="first-edit-config-fresh" data-user-id="' + esc(userId) + '" data-script-id="' + esc(scriptId) + '">' +
      `<strong>${t("ui.fresh_configuration_file")}</strong><span class="muted">${t("ui.fresh_configuration_generated")}</span></button>`;
  const body = `<p class="modal-copy">${t("ui.configuration_edit_first_time", { script: t("ui.this_script_instance") })}</p>` +
    '<div class="first-edit-chooser">' +
    freshCard +
    '<button type="button" class="chooser-card" data-action="first-edit-config-reuse" data-user-id="' + esc(userId) + '" data-script-id="' + esc(scriptId) + '">' +
    `<strong>${t("ui.reuse_configuration_file")}</strong><span class="muted">${t("ui.edit_existing_configuration_file")}</span></button>` +
    '</div>';
  const footer = `<button class="ghost" type="button" data-action="close-modal">${t("ui.cancel")}</button>`;
  showModal(modalShell(`${t("ui.first_edit")} ${t("ui.edit_configuration")}`, body, footer), false, true, true);
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
  document.title = t("ui.nexuspipeline_core") + " · " + requesterWindowToken;
  try {
    await waitForRequesterTitlePaint();
    const request = buildConfigEditRequest(mode, inputOverride, requesterWindowToken);
    await api("POST", "/api/users/" + encodeURIComponent(userId) + "/bindings/" + encodeURIComponent(scriptId) + "/edit-config", request);
  } catch (error) {
    if (error.code === "config_input_mismatch" && Array.isArray(error.data?.candidates) && error.data.candidates.length > 0) {
      const inputName = String(error.data.inputName || "");
      if (!inputName) {
        toast(t("ui.the_plugin_did_not_return_a_configuration_input_name_so_a_configuration_file_cannot_be_selected"), "error");
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
  showGlobalEditConfigCard(userId, scriptId, user?.name || "", binding?.scriptName || t("ui.script_instance"), mode);
}

/** 复用编辑候选选择：现场存在多个配置时，把候选作为编辑会话临时输入；成功保存后才写入用户绑定。 */
function openConfigCandidateChooser(userId, scriptId, mode, candidates, inputName) {
  const cards = candidates.map(candidate =>
    '<button type="button" class="chooser-card" data-action="adopt-config-candidate" data-user-id="' + esc(userId) + '" data-script-id="' + esc(scriptId) + '" data-mode="' + esc(mode) + '" data-candidate="' + esc(candidate) + '" data-input-name="' + esc(inputName) + '">' +
    '<strong class="scroll-text"><span class="scroll-inner">' + esc(candidate) + `</span></strong><span class="muted">${t("ui.configuration_candidate_use")}</span></button>`).join("");
  const body = `<p class="modal-copy">${t("ui.configuration_candidates_copy")}</p>` +
    '<div class="first-edit-chooser">' + cards + '</div>';
  const footer = `<button class="ghost" type="button" data-action="close-modal">${t("ui.cancel")}</button>`;
  showModal(modalShell(t("ui.take_over_configuration"), body, footer), false, true, true);
}

async function adoptConfigCandidate(target) {
  const userId = target.dataset.userId;
  const scriptId = target.dataset.scriptId;
  const mode = target.dataset.mode;
  const candidate = target.dataset.candidate;
  const inputName = target.dataset.inputName || "";
  try {
    if (!inputName) {
      toast(t("ui.the_configuration_input_name_is_missing_so_the_configuration_file_cannot_be_taken_over"), "error");
      return;
    }
    closeModal();
    toast(t("ui.this_edit_uses_configuration_value", { candidate }));
    await startEditConfig(userId, scriptId, mode, { name: inputName, value: candidate });
  } catch (error) {
    if (error.code === "config_input_mismatch" && Array.isArray(error.data?.candidates) && error.data.candidates.length > 0) {
      const nextInputName = String(error.data.inputName || inputName || "");
      if (!nextInputName) {
        toast(t("ui.the_plugin_did_not_return_a_configuration_input_name_so_a_configuration_file_cannot_be_selected"), "error");
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
      message = mode === "fresh" ? t("ui.config_saved_as_snapshot") : t("ui.user_configuration_saved", { user: t("ui.user") });
    } else {
      message = mode === "reuse" ? t("ui.cancelled") : t("ui.cancelled_configuration_restored");
    }
    toast(message);
    if (action === "done") {
      const validation = result?.validation;
      for (const item of Array.isArray(validation?.toasts) ? validation.toasts : []) {
        toast(item?.message || "", item?.kind || "info");
      }
      for (const item of Array.isArray(validation?.notifications) ? validation.notifications : []) {
        pushNotice(item?.title || t("ui.configuration_check_fallback"), item?.body || "", item?.kind || "info");
      }
      if (validation?.error) {
        toast(t("ui.configuration_saved_but_specialized_plugin_validation_failed"), "error");
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
  if (stateText) stateText.textContent = pressed ? t("ui.enabled") : t("ui.disabled");
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
