import { api, hydrateIcons } from "../../core/api.js";
import { $, $$ } from "../../core/dom.js";
import { esc, scriptPluginStatus, scriptPluginUnavailableMessage } from "../../core/format.js";
import { pageHeader, valueField } from "../../core/forms.js";
import { icon } from "../../core/icons.js";
import { isCurrent, registerInterval, schedule, state } from "../../core/state.js";
import { closeModal, modalShell, showModal } from "../../core/modal.js";
import { navActive, render, setFieldError, clearFieldError, setTopbarTitle, toast, withBusy } from "../../core/ui.js";
import { initDndList } from "../../core/dnd.js";
import { pluginSlotMarkup, renderPluginSlots } from "../../core/plugin-slots.js";
import { durationClock } from "../../core/duration.js";

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
  const chars = Array.from((name || "用户").trim());
  if (!chars.length) return "用";
  const first = chars[0];
  return /[\u3400-\u9fff]/.test(first) ? first : chars.slice(0, 2).join("").toUpperCase();
}

function nextRunLabel(value) {
  return value ? "正在计算倒计时" : "暂无定时任务";
}

function remainingLabel(milliseconds) {
  if (milliseconds <= 0) return "即将开始运行";
  const seconds = Math.floor(milliseconds / 1000);
  const days = Math.floor(seconds / 86400);
  const clock = durationClock(days > 0 ? seconds % 86400 : seconds);
  return days ? `${days}天 ${clock} 后运行` : `${clock} 后运行`;
}

function tickUserCountdowns() {
  const now = Date.now();
  let shouldRefresh = false;
  $$("#view .global-user-next-run[data-next-run]").forEach(element => {
    const raw = element.dataset.nextRun || "";
    if (!raw) {
      element.textContent = "暂无定时任务";
      return;
    }
    const target = new Date(raw).getTime();
    if (!Number.isFinite(target)) {
      element.textContent = "暂无定时任务";
      return;
    }
    const remaining = target - now;
    element.textContent = remaining <= 0 ? "即将开始运行" : remainingLabel(remaining);
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
  return '<button class="global-user-avatar-button" type="button" data-action="upload-user-avatar" data-user-id="' + esc(user.id) + '" aria-label="点击上传或更换' + esc(user.name) + '的头像" title="点击上传或更换头像">' +
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
  const queueTitle = user.nextQueueName ? "下次队列：" + user.nextQueueName : "";
  return '<article class="script-card global-user-card" data-dnd-id="' + esc(user.id) + '" data-testid="global-user-card">' +
    '<span class="drag-handle" role="button" tabindex="0" aria-label="拖拽调整全局用户顺序" title="拖拽排序">' + icon("grip") + "</span>" +
    avatarMarkup(user) +
    '<div class="script-main global-user-main">' +
      '<div class="script-name-row"><strong class="global-user-name">' + esc(user.name) + "</strong></div>" +
      '<div class="meta-line global-user-meta">' +
        '<span class="badge muted">已绑定 ' + bindingCount + " 个脚本</span>" +
        pluginUserBadgeMarkup(user) +
        pluginSlotMarkup("users.list.badges", "user-" + user.id, "user-plugin-slot", { mode: "list", primaryId: user.id }) +
        '<span class="badge blue global-user-next-run" data-next-run="' + esc(nextRun) + '" title="' + esc(queueTitle) + '">' + esc(nextRunLabel(nextRun)) + "</span>" +
      "</div>" +
    "</div>" +
    '<div class="global-user-actions row-actions entity-actions">' +
      '<button class="tertiary" type="button" data-action="open-user-management" data-user-id="' + esc(user.id) + '">用户管理</button>' +
      '<button class="tertiary" type="button" data-action="open-global-management" data-user-id="' + esc(user.id) + '">全局管理</button>' +
      '<button class="danger" type="button" data-action="delete-global-user" data-user-id="' + esc(user.id) + '">删除用户</button>' +
    "</div>" +
  "</article>";
}

export async function pageUsers(token) {
  if (!isCurrent("users", token)) return;
  navActive("users");
  setTopbarTitle("用户管理");
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
    if (isCurrent("users", token)) render('<div class="empty"><strong>加载用户管理失败</strong><span>' + esc(error.message) + "</span></div>");
    return;
  }
  if (!isCurrent("users", token)) return;
  state.scripts = scripts;
  state.plugins = status.plugins || [];
  state.users = users || [];
  userListBadgesByUser = new Map((Array.isArray(userListBadges) ? userListBadges : []).map(item => [item?.userId, Array.isArray(item?.badges) ? item.badges : []]));
  const limit = state.limits?.maxUsers ?? 50;
  const atLimit = state.users.length >= limit;
  const action = '<button class="primary" type="button" data-action="open-global-user-modal" data-testid="open-global-user-modal" ' + (atLimit ? "disabled" : "") + ">添加用户" + (atLimit ? "（" + state.users.length + "/" + limit + "）" : "") + "</button>";
  const sorted = state.users.slice().sort((a, b) => (a.index ?? 0) - (b.index ?? 0));
  const content = sorted.length
    ? '<section class="card list-surface"><div class="script-grid global-user-list" id="global-user-list">' + sorted.map(userCard).join("") + "</div></section>"
    : '<div class="empty"><strong>暂无用户</strong><span>点击右上角「添加用户」创建用户后，再为它绑定一个或多个脚本实例。</span></div>';
  render(pageHeader("账号管理", "用户管理", "统一管理用户头像、脚本绑定、运行优先级和通知设置。", action) + content);
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
    if (user && binding && script) showGlobalEditConfigCard(user.id, script.id, user.name, script.name);
  } catch {
    // 编辑会话恢复失败时保留页面，用户可从绑定卡片重新进入配置编辑。
  }
}

export function openGlobalUserModal() {
  const body = valueField("gu-name", "用户名 <span class='req'>*</span>", "", "text", 'placeholder="全局名称，不区分大小写"');
  showModal(modalShell("添加用户", body, '<button class="primary" type="button" data-action="save-global-user" data-testid="save-global-user">保存</button><button class="ghost" type="button" data-action="close-modal">取消</button>'), false, true);
}

export async function saveGlobalUser() {
  const name = $("#gu-name")?.value.trim() || "";
  if (!name) {
    setFieldError("gu-name", "请填写用户名");
    toast("请填写用户名", "error");
    return;
  }
  if (new TextEncoder().encode(name).length > MAX_ENTITY_NAME_BYTES) {
    setFieldError("gu-name", `用户名最多 ${MAX_ENTITY_NAME_BYTES} 字节`);
    toast(`用户名最多 ${MAX_ENTITY_NAME_BYTES} 字节`, "error");
    return;
  }
  clearFieldError("gu-name");
  try {
    await api("POST", "/api/users", { name });
    closeModal();
    toast("用户已创建");
    await reloadUsers();
  } catch (error) {
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
  const body = '<p class="modal-copy">删除「' + esc(user.name) + '」会解除全部脚本绑定并清理该用户的配置数据。请输入完整用户名确认。</p>' +
    valueField("gu-delete-name", "确认用户名 <span class='req'>*</span>", "", "text", 'placeholder="' + esc(user.name) + '"');
  showModal(modalShell("删除用户", body, '<button class="danger solid" type="button" data-action="confirm-delete-global-user" data-testid="confirm-delete-global-user">确认删除</button><button class="ghost" type="button" data-action="close-modal">取消</button>'));
}

export async function confirmDeleteGlobalUser() {
  if (!deleteDraft) return;
  const input = $("#gu-delete-name")?.value || "";
  if (input !== deleteDraft.name) {
    setFieldError("gu-delete-name", "请输入与用户名完全一致的内容");
    toast("请输入完整用户名以确认删除", "error");
    return;
  }
  try {
    await api("DELETE", "/api/users/" + encodeURIComponent(deleteDraft.id), { confirmName: input });
    const deletedName = deleteDraft.name;
    deleteDraft = null;
    closeModal();
    toast("已删除用户「" + deletedName + "」");
    await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
  }
}

export async function reorderGlobalUsers(ids) {
  try {
    await api("PUT", "/api/users/order", { ids });
    toast("用户顺序已保存");
    await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
    await reloadUsers();
  }
}

function showGlobalEditConfigCard(userId, scriptId, userName, scriptName) {
  showModal(modalShell("配置编辑中", '<p class="modal-copy">主程序已启动（不带参数）。请设置用户「' + esc(userName) + '」在脚本「' + esc(scriptName) + '」中的配置。完成后保存，或取消本次修改。</p>',
    '<button class="primary" type="button" data-action="global-edit-config-done" data-user-id="' + esc(userId) + '" data-script-id="' + esc(scriptId) + '">完成</button><button class="ghost" type="button" data-action="global-edit-config-cancel" data-user-id="' + esc(userId) + '" data-script-id="' + esc(scriptId) + '">取消</button>'), false, true);
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
    await api("POST", "/api/users/" + encodeURIComponent(userId) + "/bindings/" + encodeURIComponent(scriptId) + "/edit-config", { action: "start" });
    showGlobalEditConfigCard(userId, scriptId, user.name, binding.scriptName || "脚本实例");
  } catch (error) {
    toast(error.message, "error");
  }
}

export async function globalEditConfigAction(userId, scriptId, action) {
  try {
    await api("POST", "/api/users/" + encodeURIComponent(userId) + "/bindings/" + encodeURIComponent(scriptId) + "/edit-config", { action });
    setManagementDraft(null);
    closeModal();
    toast(action === "done" ? "用户配置已保存" : "已取消，配置已还原");
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
  if (stateText) stateText.textContent = pressed ? "已启用" : "已停用";
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
  "global-edit-config-done": target => withBusy(target, () => globalEditConfigAction(target.dataset.userId, target.dataset.scriptId, "done")),
  "global-edit-config-cancel": target => withBusy(target, () => globalEditConfigAction(target.dataset.userId, target.dataset.scriptId, "cancel")),
};
