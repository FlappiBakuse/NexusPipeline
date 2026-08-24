import { api, hydrateIcons } from "../core/api.js";
import { $, $$ } from "../core/dom.js";
import { esc, scriptFallbackIcon } from "../core/format.js";
import { pageHeader, selectField, switchControl, valueField } from "../core/forms.js";
import { icon } from "../core/icons.js";
import { isCurrent, registerInterval, schedule, state } from "../core/state.js";
import { closeModal, confirmModal, modalShell, showModal } from "../core/modal.js";
import { navActive, render, setFieldError, clearFieldError, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { initDndList } from "../core/dnd.js";

let managementDraft = null;
let deleteDraft = null;
let nextTimer = null;
let nextRefreshPending = false;

function userById(id) {
  return (state.users || []).find(user => user.id === id);
}

function cloneUser(user) {
  return JSON.parse(JSON.stringify(user));
}

function scriptById(id) {
  return (state.scripts || []).find(script => script.id === id);
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
  const hours = String(Math.floor(seconds % 86400 / 3600)).padStart(2, "0");
  const minutes = String(Math.floor(seconds % 3600 / 60)).padStart(2, "0");
  const secs = String(seconds % 60).padStart(2, "0");
  const clock = hours + ":" + minutes + ":" + secs;
  return days ? days + "天 " + clock + " 后运行" : clock + " 后运行";
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
        '<span class="badge muted">自动签到未开启 · 即将开发</span>' +
        '<span class="badge blue global-user-next-run" data-next-run="' + esc(nextRun) + '" title="' + esc(queueTitle) + '">' + esc(nextRunLabel(nextRun)) + "</span>" +
      "</div>" +
    "</div>" +
    '<div class="global-user-actions row-actions">' +
      '<button class="tertiary" type="button" data-action="open-user-management" data-user-id="' + esc(user.id) + '">用户管理</button>' +
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
  let users;
  try {
    [users, state.scripts] = await Promise.all([api("GET", "/api/users"), api("GET", "/api/scripts")]);
  } catch (error) {
    if (isCurrent("users", token)) render('<div class="empty"><strong>加载用户管理失败</strong><span>' + esc(error.message) + "</span></div>");
    return;
  }
  if (!isCurrent("users", token)) return;
  state.users = users || [];
  const limit = state.limits?.maxUsers ?? 50;
  const atLimit = state.users.length >= limit;
  const action = '<button class="primary" type="button" data-action="open-global-user-modal" ' + (atLimit ? "disabled" : "") + ">添加用户" + (atLimit ? "（" + state.users.length + "/" + limit + "）" : "") + "</button>";
  const sorted = state.users.slice().sort((a, b) => (a.index ?? 0) - (b.index ?? 0));
  const content = sorted.length
    ? '<section class="card list-surface"><div class="script-grid global-user-list" id="global-user-list">' + sorted.map(userCard).join("") + "</div></section>"
    : '<div class="empty"><strong>暂无用户</strong><span>创建用户后，再为它绑定一个或多个脚本实例。</span><button class="primary" type="button" data-action="open-global-user-modal">添加用户</button></div>';
  render(pageHeader("账号管理", "用户管理", "统一管理用户头像、脚本绑定、运行优先级和通知设置。", action) + content);
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

async function reloadUsers() {
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
  const body = valueField("gu-name", "用户名 <span class='req'>*</span>", "", "text", 'placeholder="全局名称，不区分大小写"') +
    switchControl("gu-auto", "自动签到", "该能力将在后续版本通过插件提供", false, "toggle-user-management-switch", 'data-flag="gu-auto" disabled') +
    '<p class="muted helper-copy">自动签到将在后续版本通过 Plugin API 实现。</p>';
  showModal(modalShell("添加用户", body, '<button class="primary" type="button" data-action="save-global-user">保存</button><button class="ghost" type="button" data-action="close-modal">取消</button>'));
}

export async function saveGlobalUser() {
  const name = $("#gu-name")?.value.trim() || "";
  if (!name) {
    setFieldError("gu-name", "请填写用户名");
    toast("请填写用户名", "error");
    return;
  }
  clearFieldError("gu-name");
  try {
    await api("POST", "/api/users", { name, autoCheckInEnabled: false });
    closeModal();
    toast("用户已创建");
    await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
  }
}

function availableScripts(user) {
  const boundIds = new Set((user.bindings || []).map(binding => binding.scriptInstanceId));
  return (state.scripts || [])
    .slice()
    .sort((a, b) => (a.index ?? 0) - (b.index ?? 0))
    .filter(script => !boundIds.has(script.id));
}

function bindingIdPart(id) {
  return String(id || "script").replace(/[^a-zA-Z0-9_-]/g, "-");
}

function bindingSwitch(id, label, description, pressed, field) {
  return switchControl(id, label, description, pressed, "toggle-user-management-switch", 'data-binding-field="' + esc(field) + '"');
}

function bindingMarkup(binding, index) {
  const script = (state.scripts || []).find(item => item.id === binding.scriptInstanceId);
  const scriptName = binding.scriptName || script?.name || "（脚本实例不存在）";
  const idPart = bindingIdPart(binding.scriptInstanceId);
  const enabled = binding.enabled !== false;
  const notifyEnabled = binding.notifyEnabled !== false;
  return '<details class="user-binding-card" data-binding-id="' + esc(binding.scriptInstanceId) + '"' + (index === 0 ? " open" : "") + ">" +
    '<summary class="user-binding-summary">' +
      '<span class="user-binding-summary-main">' +
        '<img class="script-ico user-binding-script-ico" src="' + scriptFallbackIcon + '" alt="" width="36" height="36" data-icon-id="' + esc(binding.scriptInstanceId) + '">' +
        '<span class="user-binding-summary-copy"><strong>' + esc(scriptName) + '</strong><span class="muted">' + (enabled ? "参与运行" : "已暂停运行") + "</span></span>" +
      "</span>" +
      '<span class="badge ' + (enabled ? "ok" : "muted") + '">' + (enabled ? "已启用" : "已停用") + "</span>" +
    "</summary>" +
    '<div class="user-binding-details">' +
      bindingSwitch("ub-" + idPart + "-enabled", "参与运行", "停用后不会参与脚本或队列运行", enabled, "enabled") +
      bindingSwitch("ub-" + idPart + "-notify", "用户运行通知", "脚本实例通知开启时才会发送", notifyEnabled, "notifyEnabled") +
      valueField("ub-" + idPart + "-smtp", "SMTP 独立收件人", binding.smtpTo || "", "text", 'data-binding-field="smtpTo" placeholder="留空继承全局收件人"') +
      '<p class="muted helper-copy binding-helper">仅 SMTP 使用；留空继承全局收件人，Webhook 不受影响。</p>' +
      '<div class="subsection"><h3>任务前运行脚本</h3>' +
        valueField("ub-" + idPart + "-pre", "脚本路径（填写则启用，留空不启用）", binding.preRunScript || "", "text", 'data-binding-field="preRunScript"') +
        bindingSwitch("ub-" + idPart + "-pre-once", "仅首次执行", "重试时不再执行", !!binding.preRunOnceOnly, "preRunOnceOnly") +
      "</div>" +
      '<div class="subsection"><h3>任务后运行脚本</h3>' +
        valueField("ub-" + idPart + "-post", "脚本路径（填写则启用，留空不启用）", binding.postRunScript || "", "text", 'data-binding-field="postRunScript"') +
        bindingSwitch("ub-" + idPart + "-post-final", "仅最终完成", "仅最终运行完成启用", !!binding.postRunOnFinalOnly, "postRunOnFinalOnly") +
      "</div>" +
      '<div class="binding-actions row-actions">' +
        '<button class="tertiary" type="button" data-action="edit-user-config-global" data-user-id="' + esc(managementDraft.userId) + '" data-script-id="' + esc(binding.scriptInstanceId) + '">编辑配置</button>' +
        '<button class="danger" type="button" data-action="delete-user-binding" data-user-id="' + esc(managementDraft.userId) + '" data-script-id="' + esc(binding.scriptInstanceId) + '">移除绑定</button>' +
      "</div>" +
    "</div>" +
  "</details>";
}

function renderUserManagementModal() {
  const draft = managementDraft;
  if (!draft) return;
  const user = draft.user;
  const scripts = availableScripts(user);
  const options = scripts.length
    ? scripts.map(script => ({ value: script.id, label: script.name }))
    : [{ value: "", label: "没有可添加的脚本实例" }];
  const bindingList = (user.bindings || []).length
    ? '<div class="user-binding-list" id="user-binding-list">' + user.bindings.map(bindingMarkup).join("") + "</div>"
    : '<div class="empty compact-empty"><strong>尚未绑定脚本实例</strong><span>从上方选择脚本后添加绑定。</span></div>';
  const body =
    '<section class="user-management-settings">' +
      valueField("um-name", "用户名 <span class='req'>*</span>", user.name, "text", 'placeholder="全局名称，不区分大小写"') +
      switchControl("um-auto", "自动签到", "该能力将在后续版本通过插件提供", false, "toggle-user-management-switch", 'data-binding-field="autoCheckInEnabled" disabled') +
      '<p class="muted helper-copy">自动签到将在后续版本通过 Plugin API 实现。</p>' +
      '<div class="user-avatar-setting">' + (user.avatarUrl ? '<span class="muted">已设置自定义头像</span><button class="tertiary" type="button" data-action="remove-user-avatar" data-user-id="' + esc(user.id) + '">移除自定义头像</button>' : '<span class="muted">当前使用用户名生成的默认头像</span>') + "</div>" +
    "</section>" +
    '<section class="subsection user-binding-section">' +
      '<div class="section-heading"><div><h3>已绑定脚本实例</h3><p class="muted">每个绑定独立保存参与运行、前后置脚本和通知配置。</p></div></div>' +
      '<div class="binding-add-row">' +
        selectField("um-script-to-add", "选择未绑定脚本", "", options, scripts.length ? "" : "disabled") +
        '<button class="primary sm" type="button" data-action="add-binding-from-management" ' + (scripts.length ? "" : "disabled") + ">添加绑定</button>" +
      "</div>" +
      bindingList +
    "</section>";
  showModal(modalShell("用户管理", body, '<button class="primary" type="button" data-action="save-user-management">保存设置</button><button class="ghost" type="button" data-action="close-modal">取消</button>'), true);
  hydrateIcons(document);
}

export function openUserManagement(userId) {
  const user = userById(userId);
  if (!user) return;
  managementDraft = { userId, user: cloneUser(user) };
  renderUserManagementModal();
}

function readBindingPayloads() {
  return Array.from(document.querySelectorAll("#user-binding-list [data-binding-id]")).map(card => {
    const read = field => card.querySelector('[data-binding-field="' + field + '"]');
    const pressed = field => read(field)?.getAttribute("aria-pressed") === "true";
    return {
      scriptInstanceId: card.dataset.bindingId,
      enabled: pressed("enabled"),
      notifyEnabled: pressed("notifyEnabled"),
      smtpTo: read("smtpTo")?.value.trim() || "",
      preRunScript: read("preRunScript")?.value.trim() || "",
      preRunOnceOnly: pressed("preRunOnceOnly"),
      postRunScript: read("postRunScript")?.value.trim() || "",
      postRunOnFinalOnly: pressed("postRunOnFinalOnly"),
    };
  });
}

export async function saveUserManagement() {
  if (!managementDraft) return;
  const name = $("#um-name")?.value.trim() || "";
  if (!name) {
    setFieldError("um-name", "请填写用户名");
    toast("请填写用户名", "error");
    return;
  }
  clearFieldError("um-name");
  const userId = managementDraft.userId;
  const bindings = readBindingPayloads();
  try {
    await api("PUT", "/api/users/" + encodeURIComponent(userId), { name, autoCheckInEnabled: false });
    for (const binding of bindings) {
      await api("PUT", "/api/users/" + encodeURIComponent(userId) + "/bindings/" + encodeURIComponent(binding.scriptInstanceId), binding);
    }
    managementDraft = null;
    closeModal();
    toast("用户设置已保存");
    await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
  }
}

async function refreshManagedUser() {
  if (!managementDraft) return;
  try {
    state.users = await api("GET", "/api/users");
    const user = userById(managementDraft.userId);
    if (!user) {
      managementDraft = null;
      closeModal();
      await reloadUsers();
      return;
    }
    managementDraft.user = cloneUser(user);
    renderUserManagementModal();
  } catch (error) {
    toast(error.message, "error");
  }
}

export async function addBindingFromManagement() {
  if (!managementDraft) return;
  const scriptId = $("#um-script-to-add")?.value || "";
  if (!scriptId) {
    toast("请选择未绑定的脚本实例", "error");
    return;
  }
  try {
    await api("POST", "/api/users/" + encodeURIComponent(managementDraft.userId) + "/bindings", {
      scriptInstanceId: scriptId,
      enabled: true,
      notifyEnabled: true,
      preRunScript: "",
      preRunOnceOnly: false,
      postRunScript: "",
      postRunOnFinalOnly: false,
      smtpTo: "",
    });
    toast("脚本绑定已添加");
    await refreshManagedUser();
  } catch (error) {
    toast(error.message, "error");
  }
}

export function deleteGlobalUser(id) {
  const user = userById(id);
  if (!user) return;
  deleteDraft = user;
  const body = '<p class="modal-copy">删除「' + esc(user.name) + '」会解除全部脚本绑定并清理该用户的配置数据。请输入完整用户名确认。</p>' +
    valueField("gu-delete-name", "确认用户名 <span class='req'>*</span>", "", "text", 'placeholder="' + esc(user.name) + '"');
  showModal(modalShell("删除用户", body, '<button class="danger solid" type="button" data-action="confirm-delete-global-user">确认删除</button><button class="ghost" type="button" data-action="close-modal">取消</button>'));
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

export function deleteUserBinding(userId, scriptId) {
  const user = userById(userId);
  const binding = user?.bindings?.find(item => item.scriptInstanceId === scriptId);
  if (!user || !binding) return;
  confirmModal("移除脚本绑定", "确定移除「" + esc(user.name) + "」与「" + esc(binding.scriptName || "该脚本实例") + "」的绑定？该绑定的配置数据会一并清理。", "confirm-delete-user-binding", { "user-id": userId, "script-id": scriptId });
}

export async function confirmDeleteUserBinding(userId, scriptId) {
  try {
    await api("DELETE", "/api/users/" + encodeURIComponent(userId) + "/bindings/" + encodeURIComponent(scriptId));
    toast("脚本绑定已移除");
    await refreshManagedUser();
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

export function uploadUserAvatar(id) {
  const input = document.createElement("input");
  input.type = "file";
  input.accept = "image/png,image/jpeg,image/webp";
  input.addEventListener("change", () => {
    const file = input.files?.[0];
    if (!file) return;
    if (!["image/png", "image/jpeg", "image/webp"].includes(file.type)) {
      toast("头像仅支持 PNG、JPEG 或 WebP", "error");
      return;
    }
    if (file.size > 5 * 1024 * 1024) {
      toast("头像文件过大（上限 5 MiB）", "error");
      return;
    }
    const reader = new FileReader();
    reader.onload = async () => {
      try {
        const dataUrl = String(reader.result || "");
        await api("POST", "/api/users/" + encodeURIComponent(id) + "/avatar", { mimeType: file.type, data: dataUrl.split(",", 2)[1] || "" });
        toast("头像已更新");
        if (managementDraft?.userId === id) await refreshManagedUser();
        else await reloadUsers();
      } catch (error) {
        toast(error.message, "error");
      }
    };
    reader.readAsDataURL(file);
  });
  input.click();
}

export async function removeUserAvatar(id) {
  try {
    await api("DELETE", "/api/users/" + encodeURIComponent(id) + "/avatar");
    toast("已恢复默认文字头像");
    if (managementDraft?.userId === id) await refreshManagedUser();
    else await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
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
    managementDraft = null;
    closeModal();
    toast(action === "done" ? "用户配置已保存" : "已取消，配置已还原");
    await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
  }
}

function toggleManagementSwitch(target) {
  if (target.disabled) return;
  const on = target.getAttribute("aria-pressed") === "true";
  target.setAttribute("aria-pressed", on ? "false" : "true");
  target.dataset.state = on ? "off" : "on";
  const stateText = target.querySelector("[data-switch-state]");
  if (stateText) stateText.textContent = on ? "已停用" : "已启用";
}

export const actions = {
  "open-global-user-modal": () => openGlobalUserModal(),
  "open-user-management": target => openUserManagement(target.dataset.userId),
  "save-global-user": target => withBusy(target, () => saveGlobalUser()),
  "save-user-management": target => withBusy(target, () => saveUserManagement()),
  "delete-global-user": target => deleteGlobalUser(target.dataset.userId),
  "confirm-delete-global-user": target => withBusy(target, () => confirmDeleteGlobalUser()),
  "add-binding-from-management": target => withBusy(target, () => addBindingFromManagement()),
  "delete-user-binding": target => deleteUserBinding(target.dataset.userId, target.dataset.scriptId),
  "confirm-delete-user-binding": target => withBusy(target, () => confirmDeleteUserBinding(target.dataset.userId, target.dataset.scriptId)),
  "upload-user-avatar": target => uploadUserAvatar(target.dataset.userId),
  "remove-user-avatar": target => removeUserAvatar(target.dataset.userId),
  "edit-user-config-global": target => editGlobalUserConfig(target.dataset.userId, target.dataset.scriptId),
  "global-edit-config-done": target => withBusy(target, () => globalEditConfigAction(target.dataset.userId, target.dataset.scriptId, "done")),
  "global-edit-config-cancel": target => withBusy(target, () => globalEditConfigAction(target.dataset.userId, target.dataset.scriptId, "cancel")),
  "toggle-user-management-switch": target => toggleManagementSwitch(target),
};
