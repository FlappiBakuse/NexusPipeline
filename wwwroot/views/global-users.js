import { api, hydrateIcons } from "../core/api.js";
import { $, $$ } from "../core/dom.js";
import { esc, scriptFallbackIcon, scriptPluginStatus, scriptPluginUnavailableMessage } from "../core/format.js";
import { pageHeader, selectField, switchControl, textareaField, valueField } from "../core/forms.js";
import { pluginMultiSelectMarkup, selectedPluginMultiSelectValues, syncPluginMultiSelect } from "../core/plugin-fields.js";
import { icon } from "../core/icons.js";
import { isCurrent, registerInterval, schedule, state } from "../core/state.js";
import { closeModal, confirmModal, modalShell, showModal } from "../core/modal.js";
import { navActive, render, setFieldError, clearFieldError, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { initDndList } from "../core/dnd.js";

let managementDraft = null;
let deleteDraft = null;
let globalManagementDraft = null;
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

function unavailableScriptMessage(scriptId) {
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
  let users, scripts, status;
  try {
    [users, scripts, status] = await Promise.all([api("GET", "/api/users"), api("GET", "/api/scripts"), api("GET", "/api/status")]);
  } catch (error) {
    if (isCurrent("users", token)) render('<div class="empty"><strong>加载用户管理失败</strong><span>' + esc(error.message) + "</span></div>");
    return;
  }
  if (!isCurrent("users", token)) return;
  state.scripts = scripts;
  state.plugins = status.plugins || [];
  state.users = users || [];
  const limit = state.limits?.maxUsers ?? 50;
  const atLimit = state.users.length >= limit;
  const action = '<button class="primary" type="button" data-action="open-global-user-modal" data-testid="open-global-user-modal" ' + (atLimit ? "disabled" : "") + ">添加用户" + (atLimit ? "（" + state.users.length + "/" + limit + "）" : "") + "</button>";
  const sorted = state.users.slice().sort((a, b) => (a.index ?? 0) - (b.index ?? 0));
  const content = sorted.length
    ? '<section class="card list-surface"><div class="script-grid global-user-list" id="global-user-list">' + sorted.map(userCard).join("") + "</div></section>"
    : '<div class="empty"><strong>暂无用户</strong><span>点击右上角「添加用户」创建用户后，再为它绑定一个或多个脚本实例。</span></div>';
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

function availableScripts(user) {
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

function bindingIdPart(id) {
  return String(id || "script").replace(/[^a-zA-Z0-9_-]/g, "-");
}

/** 紧凑开关：仅轨道+滑块，用于展开卡片头部等窄空间；标签经 aria-label 表达。 */
function umSwitch(id, label, pressed, field) {
  return `<button id="${esc(id)}" class="mode-toggle switch-control" type="button" aria-label="${esc(label)}" aria-pressed="${pressed ? "true" : "false"}" data-state="${pressed ? "on" : "off"}" data-toggle-text="false" data-action="toggle-user-management-switch" data-binding-field="${esc(field)}"><span class="switch-track" aria-hidden="true"><span class="switch-thumb"></span></span><span class="sr-only" data-switch-state>${pressed ? "已启用" : "已停用"}</span></button>`;
}

function umScriptName(binding) {
  const script = (state.scripts || []).find(item => item.id === binding.scriptInstanceId);
  return binding.scriptName || script?.name || "（脚本实例不存在）";
}

function umBadges(binding) {
  const effective = binding.effective || binding;
  const runDays = typeof effective.runDays === "number" ? effective.runDays : -1;
  const enabled = effective.enabled !== false && runDays !== 0;
  const stateBadge = `<span class="badge ${enabled ? "ok" : "muted"}">${enabled ? "已启用" : "已停用"}</span>`;
  const daysBadge = runDays === 0
    ? '<span class="badge warn">运行已停止</span>'
    : runDays > 0
      ? `<span class="badge blue">剩余 ${runDays} 天</span>`
      : '<span class="badge muted">永久运行</span>';
  const script = scriptById(binding.scriptInstanceId);
  const pluginStatus = script ? scriptPluginStatus(script, state.plugins || []) : null;
  const pluginBadge = pluginStatus?.missing
    ? '<span class="badge bad">未知专项</span>'
    : pluginStatus?.specialized && !pluginStatus.available
      ? '<span class="badge warn">专项插件不可用</span>'
      : "";
  return pluginBadge + stateBadge + daysBadge;
}

function umBindingCardMarkup(binding) {
  const idPart = bindingIdPart(binding.scriptInstanceId);
  const name = umScriptName(binding);
  const unavailableMessage = unavailableScriptMessage(binding.scriptInstanceId);
  const unavailable = !!unavailableMessage;
  const effective = binding.effective || binding;
  const locks = binding.locks || {};
  const notifyEnabled = effective.notifyEnabled !== false;
  const runDays = typeof effective.runDays === "number" ? effective.runDays : -1;
  const enabled = effective.enabled !== false;
  const preValue = (effective.preRunOnceOnly ? "%FIRST% " : "") + (effective.preRunScript || "");
  const postValue = (effective.postRunOnFinalOnly ? "%LAST% " : "") + (effective.postRunScript || "");
  const overrideHelper = category => locks[category]
    ? '<p class="muted helper-copy um-override-helper">由「全局管理」同步 / 关闭全局同步后将恢复此脚本实例原有设置</p>'
    : "";
  const runDaysPlaceholder = "填写 -1 永久运行；填写 0 则不运行该脚本实例；填写 0 以上的数字则运行，每日减 1。";
  const dragEnabled = umBindingDragEnabled();
  const dragHidden = umState.bindingEditMode || !!umState.expandedId;
  const head =
    '<div class="um-binding-head">' +
      '<span class="drag-handle um-binding-drag-handle" role="button" tabindex="' + (dragEnabled ? "0" : "-1") + '" aria-disabled="' + (dragEnabled ? "false" : "true") + '"' + (dragHidden ? " hidden" : "") + ' aria-label="拖拽排序（方向键调整顺序）" title="拖拽排序" data-testid="um-binding-drag-handle">' + icon("grip") + "</span>" +
      '<button class="um-binding-toggle' + (unavailable ? ' is-unavailable' : '') + '" type="button" data-action="toggle-um-binding" aria-expanded="false" aria-label="' + esc(unavailable ? '无法识别的专项脚本实例' : '打开脚本实例设置') + '：' + esc(name) + '"' + (unavailable ? ' aria-disabled="true" title="' + esc(unavailableMessage) + '"' : '') + (umState.bindingEditMode ? ' disabled aria-disabled="true"' : '') + '>' +
        '<img class="script-ico um-binding-ico" src="' + esc(scriptFallbackIcon) + '" alt="" width="36" height="36" loading="lazy" data-icon-id="' + esc(binding.scriptInstanceId) + '">' +
        '<span class="um-binding-copy"><strong class="um-binding-name">' + esc(name) + '</strong><span class="um-binding-badges">' + umBadges(binding) + "</span></span>" +
      "</button>" +
      '<button class="danger um-binding-remove" type="button" data-action="delete-user-binding" data-testid="um-remove-binding" data-user-id="' + esc(managementDraft.userId) + '" data-script-id="' + esc(binding.scriptInstanceId) + '">移除绑定</button>' +
      '<span class="um-binding-bottom-arrow" aria-hidden="true">' + icon("chevronDown") + "</span>" +
    "</div>";
  const subhead =
    '<button class="um-binding-subhead" type="button" data-action="user-management-back" aria-label="返回上级，点击标题区域返回">' +
      '<img class="script-ico um-binding-ico" src="' + esc(scriptFallbackIcon) + '" alt="" width="28" height="28" loading="lazy" data-icon-id="' + esc(binding.scriptInstanceId) + '">' +
      '<strong class="um-binding-subname">' + esc(name) + '</strong>' +
      '<span class="um-binding-subtitle" data-subtitle="general">·通用选项</span>' +
      '<span class="um-binding-subtitle" data-subtitle="notify">·通知推送选项</span>' +
      '<span class="um-binding-subtitle" data-subtitle="advanced">·高级选项</span>' +
      '<span class="um-binding-bottom-arrow" aria-hidden="true">' + icon("chevronDown") + "</span>" +
    "</button>";
  const mainView =
    '<div class="um-view um-view-main">' +
      '<button class="um-edit-config' + (unavailable ? ' is-unavailable' : '') + '" type="button" data-action="edit-user-config-global" data-user-id="' + esc(managementDraft.userId) + '" data-script-id="' + esc(binding.scriptInstanceId) + '"' + (unavailable ? ' title="' + esc(unavailableMessage) + '"' : '') + '>' +
        '<span class="um-edit-config-copy"><strong>编辑配置</strong><span class="muted">启动主程序打开该脚本实例的用户配置</span></span>' +
        '<span class="um-edit-config-arrow">' + icon("chevronRight") + "</span>" +
      "</button>" +
      '<div class="um-option-grid">' +
        '<button class="um-option-card" type="button" data-action="set-um-subview" data-view="general"><strong>通用选项</strong><span class="muted">绑定启用状态与运行天数</span>' + icon("chevronRight", "um-option-arrow") + "</button>" +
        '<button class="um-option-card" type="button" data-action="set-um-subview" data-view="notify"><strong>通知推送选项</strong><span class="muted">用户级通知开关与 SMTP 收件人</span>' + icon("chevronRight", "um-option-arrow") + "</button>" +
        '<button class="um-option-card" type="button" data-action="set-um-subview" data-view="advanced"><strong>高级选项</strong><span class="muted">任务前后脚本</span>' + icon("chevronRight", "um-option-arrow") + "</button>" +
      "</div>" +
    "</div>";
  const generalView =
    '<div class="um-view um-view-general">' +
      switchControl("um-" + idPart + "-enabled", "是否启用", "运行天数为 0 时不会参与运行", enabled, "toggle-user-management-switch", 'data-binding-field="enabled"' + (locks.general ? " disabled" : "")) +
      valueField("um-" + idPart + "-run-days", "运行天数", runDays, "number", 'data-binding-field="runDays" min="-1" step="1" placeholder="' + esc(runDaysPlaceholder) + '"' + (locks.general ? " disabled" : "")) +
      overrideHelper("general") +
    "</div>";
  const notifyView =
    '<div class="um-view um-view-notify">' +
      switchControl("um-" + idPart + "-notify", "开启通知推送", "脚本实例通知开启时才会发送", notifyEnabled, "toggle-user-management-switch", 'data-binding-field="notifyEnabled"' + (locks.notification ? " disabled" : "")) +
      valueField("um-" + idPart + "-smtp", "SMTP 收件人", effective.smtpTo || "", "text", 'data-binding-field="smtpTo" placeholder="留空继承全局收件人"' + (locks.notification ? " disabled" : "")) +
      '<p class="muted helper-copy">仅 SMTP 使用；留空继承全局收件人，Webhook 不受影响。</p>' +
      overrideHelper("notification") +
    "</div>";
  const advancedView =
    '<div class="um-view um-view-advanced">' +
      valueField("um-" + idPart + "-pre", "任务前运行脚本路径", preValue, "text", 'data-binding-field="preRunScript" placeholder="%FIRST% 开头填写仅首次运行"' + (locks.advanced ? " disabled" : "")) +
      valueField("um-" + idPart + "-post", "任务后运行脚本路径", postValue, "text", 'data-binding-field="postRunScript" placeholder="%LAST% 开头填写仅最终运行"' + (locks.advanced ? " disabled" : "")) +
      overrideHelper("advanced") +
    "</div>";
  return '<article class="um-binding-card' + (umState.bindingEditMode ? ' is-binding-editing' : '') + (unavailable ? ' is-unavailable' : '') + '" data-testid="um-binding-card" data-dnd-id="' + esc(binding.scriptInstanceId) + '" data-binding-id="' + esc(binding.scriptInstanceId) + '" data-binding-enabled="' + (enabled ? "true" : "false") + '" data-subview="main"' + (unavailable ? ' data-plugin-unavailable="true"' : '') + '>' +
    head + subhead +
    '<div class="um-binding-body">' + mainView + generalView + notifyView + advancedView + "</div>" +
  "</article>";
}

/** 用户管理界面状态：展开卡片、二级页、添加脚本面板与多选集合。 */
const umState = {
  expandedId: null,
  subview: "main",
  addOpen: false,
  bindingEditMode: false,
  addSelected: new Set(),
};

function umBindingDragEnabled() {
  return !umState.bindingEditMode && !umState.expandedId && !umState.addOpen;
}

function umAddItemMarkup(script) {
  const selected = umState.addSelected.has(script.id);
  return '<button class="um-add-item" type="button" data-action="toggle-um-add-item" data-script-id="' + esc(script.id) + '" aria-pressed="' + (selected ? "true" : "false") + '">' +
    '<img class="script-ico" src="' + esc(scriptFallbackIcon) + '" alt="" width="32" height="32" loading="lazy" data-icon-id="' + esc(script.id) + '">' +
    '<span class="um-add-item-copy"><strong>' + esc(script.name) + "</strong>" + (script.pluginType ? '<span class="muted">专项脚本</span>' : "") + "</span>" +
    '<span class="um-add-item-mark" aria-hidden="true">' + icon("check") + "</span>" +
  "</button>";
}

function mergeManagedBinding(user, binding) {
  if (!user || !binding?.scriptInstanceId) return;
  const bindings = Array.isArray(user.bindings) ? user.bindings : [];
  const index = bindings.findIndex(item => item.scriptInstanceId === binding.scriptInstanceId);
  if (index >= 0) bindings[index] = { ...bindings[index], ...binding };
  else bindings.push(binding);
  user.bindings = bindings;
  user.bindingCount = bindings.length;
}

function renderUserManagementModal() {
  const draft = managementDraft;
  if (!draft) return;
  const user = draft.user;
  const scripts = availableScripts(user);
  const addItems = scripts.length
    ? '<div class="um-add-grid" id="um-add-grid">' + scripts.map(umAddItemMarkup).join("") + "</div>"
    : '<div class="empty compact-empty"><strong>没有可添加的脚本实例</strong><span>所有脚本实例都已绑定。</span></div>';
  const addArea =
    '<div class="um-add-area"' + (umState.addOpen ? " data-open" : "") + ">" +
      '<button class="um-add-script" type="button" data-action="toggle-um-add-panel" data-testid="um-add-script">' + icon("plus") + "<span>添加脚本</span></button>" +
      '<div class="um-add-panel" data-testid="um-add-panel">' +
        '<div class="um-add-head"><h4>选择要绑定的脚本实例</h4><span class="muted">可多选</span></div>' +
        addItems +
        '<div class="um-add-actions"><button class="ghost" type="button" data-action="close-um-add-panel">取消</button><button class="primary" type="button" data-action="confirm-um-add-bindings" data-testid="um-add-confirm">确认</button></div>' +
      "</div>" +
    "</div>";
  const bindings = Array.isArray(user.bindings) ? user.bindings : [];
  const bindingList = bindings.length
    ? '<div class="um-bindings" id="um-binding-list">' + bindings.map(umBindingCardMarkup).join("") + "</div>"
    : '<div class="empty compact-empty"><strong>尚未绑定脚本实例</strong><span>从上方「添加脚本」选择脚本实例后添加绑定。</span></div>';
  const bindingEditToggle = '<button class="ghost sm um-binding-edit-toggle" type="button" data-action="toggle-um-binding-edit" aria-pressed="' + (umState.bindingEditMode ? "true" : "false") + '"' + (umState.expandedId ? " hidden" : "") + '>' + (umState.bindingEditMode ? "完成编辑" : "编辑绑定") + "</button>";
  const body =
    '<section class="user-management-settings">' +
      valueField("um-name", "用户名 <span class='req'>*</span>", user.name, "text", 'placeholder="全局名称，不区分大小写"') +
      textareaField("um-remark", "备注", user.remark || "", 'rows="3"', "为用户添加备注信息（可选）") +
      (user.avatarUrl ? '<div class="user-avatar-setting"><span class="muted">已设置自定义头像</span><button class="tertiary" type="button" data-action="remove-user-avatar" data-user-id="' + esc(user.id) + '">移除自定义头像</button></div>' : "") +
    "</section>" +
    '<section class="subsection user-binding-section">' +
      '<div class="section-heading um-binding-section-heading"><div><h3>已绑定脚本实例</h3><p class="muted">每个绑定独立保存运行、通知和高级选项设置。</p></div>' + bindingEditToggle + "</div>" +
      addArea +
      bindingList +
    "</section>";
  const footer = `<button class="primary" type="button" data-action="save-user-management">保存</button><button class="ghost user-management-back" type="button" data-action="user-management-back">${umState.expandedId ? "返回上级" : "取消"}</button>`;
  showModal(modalShell("用户管理", body, footer), true, true);
  syncUmState();
  wireManagedBindingDnd();
  hydrateIcons(document);
}

function wireManagedBindingDnd() {
  const list = document.getElementById("um-binding-list");
  if (!list) return;
  initDndList(list, {
    canDrag: () => umBindingDragEnabled(),
    axis: "both",
    onDrop: ids => reorderManagedBindings(ids),
  });
}

/** 根据 umState 应用到 DOM（不重建弹窗，保留输入值与滚动位置）。 */
function syncUmState() {
  const section = document.querySelector(".user-binding-section");
  if (section) {
    section.classList.toggle("um-section-expanding", !!umState.expandedId);
    section.classList.toggle("um-binding-editing", umState.bindingEditMode);
  }
  const editToggle = section?.querySelector(".um-binding-edit-toggle");
  if (editToggle) {
    editToggle.hidden = !!umState.expandedId;
    editToggle.textContent = umState.bindingEditMode ? "完成编辑" : "编辑绑定";
    editToggle.setAttribute("aria-pressed", umState.bindingEditMode ? "true" : "false");
  }
  const footerBack = document.querySelector(".user-management-back");
  if (footerBack) footerBack.textContent = umState.expandedId ? "返回上级" : "取消";
  const list = document.getElementById("um-binding-list");
  if (list) {
    const dragEnabled = umBindingDragEnabled();
    Array.from(list.querySelectorAll(".um-binding-card")).forEach(card => {
      const expanded = card.dataset.bindingId === umState.expandedId;
      card.classList.toggle("is-expanded", expanded);
      card.classList.toggle("is-binding-editing", umState.bindingEditMode);
      card.dataset.subview = expanded ? umState.subview : "main";
      const toggle = card.querySelector(".um-binding-toggle");
      if (toggle) {
        const unavailable = card.dataset.pluginUnavailable === "true";
        toggle.disabled = umState.bindingEditMode;
        toggle.setAttribute("aria-disabled", unavailable || umState.bindingEditMode ? "true" : "false");
        toggle.setAttribute("aria-expanded", expanded ? "true" : "false");
      }
      const dragHandle = card.querySelector(".um-binding-drag-handle");
      if (dragHandle) {
        dragHandle.tabIndex = dragEnabled ? 0 : -1;
        dragHandle.setAttribute("aria-disabled", dragEnabled ? "false" : "true");
        const dragHidden = umState.bindingEditMode || !!umState.expandedId;
        dragHandle.hidden = dragHidden;
        dragHandle.setAttribute("aria-hidden", dragHidden ? "true" : "false");
      }
    });
  }
  const area = document.querySelector(".um-add-area");
  if (area) {
    // 注意：dataset.open = undefined 会写入字符串 "undefined"（[data-open] 仍命中），必须用 removeAttribute。
    if (umState.addOpen) area.dataset.open = "";
    else area.removeAttribute("data-open");
    const scriptButton = area.querySelector(".um-add-script");
    if (scriptButton) scriptButton.setAttribute("aria-expanded", umState.addOpen ? "true" : "false");
  }
  document.querySelectorAll(".um-add-item").forEach(item => {
    item.setAttribute("aria-pressed", umState.addSelected.has(item.dataset.scriptId) ? "true" : "false");
  });
}

export function openUserManagement(userId) {
  const user = userById(userId);
  if (!user) return;
  managementDraft = { userId, user: cloneUser(user) };
  umState.expandedId = null;
  umState.subview = "main";
  umState.addOpen = false;
  umState.bindingEditMode = false;
  umState.addSelected.clear();
  renderUserManagementModal();
}

function readBindingPayloads() {
  return Array.from(document.querySelectorAll("#um-binding-list [data-binding-id]")).map(card => {
    const read = field => card.querySelector('[data-binding-field="' + field + '"]');
    const pressed = field => read(field)?.getAttribute("aria-pressed") === "true";
    const raw = managementDraft?.user?.bindings?.find(item => item.scriptInstanceId === card.dataset.bindingId) || {};
    const locks = raw.locks || {};
    const rawRunDays = parseInt((read("runDays")?.value || "-1"), 10);
    const runDays = Number.isNaN(rawRunDays) ? -1 : rawRunDays;
    const payload = {
      scriptInstanceId: card.dataset.bindingId,
      enabled: locks.general ? raw.enabled !== false : pressed("enabled"),
      notifyEnabled: locks.notification ? raw.notifyEnabled !== false : pressed("notifyEnabled"),
      smtpTo: locks.notification ? (raw.smtpTo || "") : (read("smtpTo")?.value.trim() || ""),
      preRunScript: locks.advanced ? (raw.preRunScript || "") : (read("preRunScript")?.value || "").trim(),
      preRunOnceOnly: locks.advanced ? raw.preRunOnceOnly === true : (read("preRunScript")?.value || "").trim().startsWith("%FIRST%"),
      postRunScript: locks.advanced ? (raw.postRunScript || "") : (read("postRunScript")?.value || "").trim(),
      postRunOnFinalOnly: locks.advanced ? raw.postRunOnFinalOnly === true : (read("postRunScript")?.value || "").trim().startsWith("%LAST%"),
      runDays: locks.general ? (typeof raw.runDays === "number" ? raw.runDays : -1) : runDays,
    };
    return payload;
  });
}

/** 保存前把 %FIRST%/%LAST% 前缀从路径字段中剥离，仅保留开关语义。 */
function normalizePrePost(payload) {
  const pre = (payload.preRunScript || "").replace(/^%FIRST%\s*/, "").trim();
  const post = (payload.postRunScript || "").replace(/^%LAST%\s*/, "").trim();
  return { ...payload, preRunScript: pre, postRunScript: post };
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
  const remark = $("#um-remark")?.value.trim() || "";
  const userId = managementDraft.userId;
  const bindings = readBindingPayloads().map(normalizePrePost);
  try {
    await api("PUT", "/api/users/" + encodeURIComponent(userId), { name, remark });
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

async function refreshManagedUser(knownBindings = []) {
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
    for (const binding of knownBindings) mergeManagedBinding(user, binding);
    managementDraft.user = cloneUser(user);
    umState.expandedId = null;
    umState.subview = "main";
    umState.addOpen = false;
    umState.addSelected.clear();
    renderUserManagementModal();
  } catch (error) {
    // 新增绑定的 POST 已成功时，即使后续列表刷新失败也先把服务端返回的卡片呈现出来。
    if (managementDraft && knownBindings.length) {
      for (const binding of knownBindings) mergeManagedBinding(managementDraft.user, binding);
      umState.expandedId = null;
      umState.subview = "main";
      umState.addOpen = false;
      umState.addSelected.clear();
      renderUserManagementModal();
    }
    toast(error.message, "error");
  }
}

function restoreManagedBindingOrder() {
  const list = document.getElementById("um-binding-list");
  const bindings = managementDraft?.user?.bindings;
  if (!list || !Array.isArray(bindings)) return;
  const cards = new Map(Array.from(list.querySelectorAll("[data-dnd-id]")).map(card => [card.dataset.dndId, card]));
  for (const binding of bindings) {
    const card = cards.get(binding.scriptInstanceId);
    if (card) list.appendChild(card);
  }
}

async function reorderManagedBindings(ids) {
  const draft = managementDraft;
  if (!draft) return;
  const userId = draft.userId;
  const currentBindings = Array.isArray(draft.user.bindings) ? draft.user.bindings : [];
  const byId = new Map(currentBindings.map(binding => [binding.scriptInstanceId, binding]));
  const orderedBindings = ids.map(id => byId.get(id)).filter(Boolean);
  if (orderedBindings.length !== currentBindings.length) {
    restoreManagedBindingOrder();
    toast("绑定脚本实例顺序无效", "error");
    return;
  }
  try {
    await api("PUT", "/api/users/" + encodeURIComponent(userId) + "/bindings/order", { ids });
    draft.user.bindings = orderedBindings;
    const cachedUser = userById(userId);
    if (cachedUser) {
      const cachedById = new Map((cachedUser.bindings || []).map(binding => [binding.scriptInstanceId, binding]));
      cachedUser.bindings = ids.map(id => cachedById.get(id)).filter(Boolean);
    }
    if (managementDraft === draft) toast("已绑定脚本实例顺序已保存");
  } catch (error) {
    restoreManagedBindingOrder();
    toast(error.message, "error");
  }
}

/** 添加脚本面板开合（展开绑定卡片时自动关闭）。 */
export function toggleUmAddPanel() {
  if (umState.bindingEditMode) return;
  umState.addOpen = !umState.addOpen;
  umState.addSelected.clear();
  if (umState.addOpen) {
    umState.expandedId = null;
    umState.subview = "main";
  }
  syncUmState();
}

export function closeUmAddPanel() {
  umState.addOpen = false;
  umState.addSelected.clear();
  syncUmState();
}

export function toggleUmAddItem(target) {
  const id = target.dataset.scriptId;
  if (umState.addSelected.has(id)) umState.addSelected.delete(id);
  else umState.addSelected.add(id);
  syncUmState();
}

export async function confirmUmAddBindings() {
  if (!managementDraft) return;
  const ids = Array.from(umState.addSelected);
  if (!ids.length) {
    toast("请选择要绑定的脚本实例", "error");
    return;
  }
  const unavailableScript = ids
    .map(scriptById)
    .find(script => script && scriptPluginStatus(script, state.plugins || []).specialized && !scriptPluginStatus(script, state.plugins || []).available);
  if (unavailableScript) {
    toast(scriptPluginUnavailableMessage(unavailableScript, state.plugins || []), "error");
    return;
  }
  const addedBindings = [];
  try {
    for (const scriptId of ids) {
      const payload = {
        scriptInstanceId: scriptId,
        enabled: true,
        notifyEnabled: true,
        preRunScript: "",
        preRunOnceOnly: false,
        postRunScript: "",
        postRunOnFinalOnly: false,
        smtpTo: "",
        runDays: -1,
      };
      addedBindings.push((await api("POST", "/api/users/" + encodeURIComponent(managementDraft.userId) + "/bindings", payload)) || payload);
    }
    toast(ids.length > 1 ? "已绑定 " + ids.length + " 个脚本实例" : "脚本绑定已添加");
    await refreshManagedUser(addedBindings);
  } catch (error) {
    if (addedBindings.length) await refreshManagedUser(addedBindings);
    toast(error.message, "error");
  }
}

/** 展开/收回绑定卡片：只能同时展开一个，展开时其他卡片与添加脚本面板自动隐藏。 */
export function toggleUmBinding(target) {
  const unavailableMessage = unavailableScriptMessage(target.closest(".um-binding-card")?.dataset.bindingId);
  if (unavailableMessage) {
    toast(unavailableMessage, "error");
    return;
  }
  if (umState.bindingEditMode || umState.addOpen) return;
  const card = target.closest(".um-binding-card");
  if (!card) return;
  const id = card.dataset.bindingId;
  if (umState.expandedId === id) {
    umState.expandedId = null;
    umState.subview = "main";
  } else {
    umState.expandedId = id;
    umState.subview = "main";
    umState.addOpen = false;
  }
  syncUmState();
}

/** 「返回上级」：收回展开的绑定卡片，还原 1/2 网格布局（效果同再次点击卡片收起）。 */
export function collapseUmBinding() {
  umState.expandedId = null;
  umState.subview = "main";
  umState.addOpen = false;
  syncUmState();
}

/** 用户管理弹窗底部返回：二级页返回绑定主卡，主卡返回绑定列表，普通状态关闭弹窗。 */
export function userManagementBack() {
  if (!umState.expandedId) {
    closeModal();
    return;
  }
  if (umState.subview !== "main") {
    umState.subview = "main";
    syncUmState();
    return;
  }
  collapseUmBinding();
}

/** 切换绑定编辑模式：编辑模式只允许移除绑定，不允许进入绑定设置。 */
export function toggleUmBindingEdit() {
  if (umState.expandedId) return;
  umState.bindingEditMode = !umState.bindingEditMode;
  if (umState.bindingEditMode) {
    umState.addOpen = false;
    umState.addSelected.clear();
  }
  syncUmState();
}

/** 展开卡片内的二级页切换（main/notify/advanced）。 */
export function setUmSubview(target) {
  const card = target.closest(".um-binding-card");
  if (umState.bindingEditMode || !card || card.dataset.bindingId !== umState.expandedId) return;
  umState.subview = target.dataset.view || "main";
  syncUmState();
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
    managementDraft = null;
    closeModal();
    toast(action === "done" ? "用户配置已保存" : "已取消，配置已还原");
    await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
  }
}

function globalFieldId(prefix, key) {
  return prefix + String(key || "field").replace(/[^a-zA-Z0-9_-]/g, "-");
}

function globalPrePostValue(script, once, marker) {
  return (once ? marker + " " : "") + (script || "");
}

function globalManagementHostMarkup(settings) {
  const general = settings.general || {};
  const notification = settings.notification || {};
  const advanced = settings.advanced || {};
  const pre = globalPrePostValue(advanced.preRunScript, advanced.preRunOnceOnly, "%FIRST%");
  const post = globalPrePostValue(advanced.postRunScript, advanced.postRunOnFinalOnly, "%LAST%");
  return '<div class="global-management-grid">' +
    '<section class="global-management-card"><div class="section-heading"><div><h3>通用</h3><p class="muted">统一控制所有脚本绑定的启用状态与运行天数。</p></div></div>' +
      switchControl("gm-general-sync", "同步通用设置", "开启后覆盖每个脚本绑定的通用设置", general.syncEnabled === true, "toggle-global-management-switch", 'data-global-field="general.syncEnabled"') +
      switchControl("gm-general-enabled", "是否启用", "关闭后所有绑定均不参与运行", general.enabled !== false, "toggle-global-management-switch", 'data-global-field="general.enabled"') +
      valueField("gm-general-run-days", "运行天数", typeof general.runDays === "number" ? general.runDays : -1, "number", 'data-global-field="general.runDays" min="-1" step="1" placeholder="-1 表示永久运行"') +
    '</section>' +
    '<section class="global-management-card"><div class="section-heading"><div><h3>通知</h3><p class="muted">统一控制所有脚本绑定的通知开关与 SMTP 收件人。</p></div></div>' +
      switchControl("gm-notification-sync", "同步通知设置", "开启后覆盖每个脚本绑定的通知设置", notification.syncEnabled === true, "toggle-global-management-switch", 'data-global-field="notification.syncEnabled"') +
      switchControl("gm-notification-enabled", "开启通知推送", "脚本实例通知开启时才会发送", notification.notifyEnabled !== false, "toggle-global-management-switch", 'data-global-field="notification.notifyEnabled"') +
      valueField("gm-notification-smtp", "SMTP 收件人", notification.smtpTo || "", "text", 'data-global-field="notification.smtpTo" placeholder="留空继承全局收件人"') +
    '</section>' +
    '<section class="global-management-card global-management-card-wide"><div class="section-heading"><div><h3>高级</h3><p class="muted">统一控制所有脚本绑定的任务前后脚本。</p></div></div>' +
      switchControl("gm-advanced-sync", "同步高级设置", "开启后覆盖每个脚本绑定的高级设置", advanced.syncEnabled === true, "toggle-global-management-switch", 'data-global-field="advanced.syncEnabled"') +
      valueField("gm-advanced-pre", "任务前运行脚本路径", pre, "text", 'data-global-field="advanced.preRunScript" placeholder="%FIRST% 开头填写仅首次运行"') +
      valueField("gm-advanced-post", "任务后运行脚本路径", post, "text", 'data-global-field="advanced.postRunScript" placeholder="%LAST% 开头填写仅最终运行"') +
    '</section>' +
  '</div>';
}

function pluginFieldMarkup(contribution, field) {
  const prefix = "gm-plugin-" + String(contribution.pluginName || "plugin").replace(/[^a-zA-Z0-9_-]/g, "-") + "-" + String(contribution.id || "settings").replace(/[^a-zA-Z0-9_-]/g, "-") + "-";
  const id = globalFieldId(prefix, field.key);
  const type = String(field.type || "text").toLowerCase();
  const value = contribution.values?.[field.key];
  const description = field.description ? '<span class="muted plugin-field-description">' + esc(field.description) + "</span>" : "";
  const required = field.required ? ' <span class="req">*</span>' : "";
  const readOnly = field.readOnly ? " disabled" : "";
  if (type === "switch") {
    return switchControl(id, esc(field.label) + required, "", value === true, "toggle-global-plugin-switch", 'data-plugin-field="' + esc(field.key) + '" data-plugin-type="switch"' + readOnly, field.label) + description;
  }
  if (type === "textarea") {
    return '<div class="field plugin-field"><label class="field-label" for="' + esc(id) + '">' + esc(field.label) + required + '</label><textarea id="' + esc(id) + '" class="form-textarea" data-plugin-field="' + esc(field.key) + '" data-plugin-type="textarea"' + (field.maxLength > 0 ? ' maxlength="' + esc(field.maxLength) + '"' : "") + ' placeholder="' + esc(field.placeholder || "") + '"' + readOnly + '>' + esc(typeof value === "string" ? value : "") + '</textarea>' + description + '</div>';
  }
  if (type === "select") {
    const options = Array.isArray(field.options) ? field.options : [];
    return selectField(id, esc(field.label) + required, typeof value === "string" ? value : "", options, 'data-plugin-field="' + esc(field.key) + '" data-plugin-type="select"' + (field.readOnly ? " disabled" : "")) + description;
  }
  if (type === "multi-select") {
    return pluginMultiSelectMarkup(id, field, value);
  }
  if (type === "secret") {
    const configured = value?.configured === true;
    return '<div class="field plugin-field plugin-secret-field"><label class="field-label" for="' + esc(id) + '">' + esc(field.label) + required + '</label><div class="plugin-secret-row"><input id="' + esc(id) + '" type="password" data-plugin-field="' + esc(field.key) + '" data-plugin-type="secret" data-secret-action="keep" maxlength="' + esc(field.maxLength > 0 ? field.maxLength : 16384) + '" placeholder="' + esc(configured ? "已设置，留空保持不变" : (field.placeholder || "请输入密钥")) + '"' + readOnly + '>' + (configured && !field.readOnly ? '<button class="tertiary" type="button" data-action="clear-plugin-secret" data-plugin-field="' + esc(field.key) + '">清除</button>' : "") + '</div>' + description + '</div>';
  }
  if (type === "status") {
    return '<div class="field plugin-field"><span class="field-label">' + esc(field.label) + '</span><span class="plugin-status-value" data-plugin-field="' + esc(field.key) + '" data-plugin-type="status">' + esc(typeof value === "string" ? value : "暂无状态") + '</span>' + description + '</div>';
  }
  return valueField(id, esc(field.label) + required, typeof value === "string" ? value : "", "text", 'data-plugin-field="' + esc(field.key) + '" data-plugin-type="text" maxlength="' + esc(field.maxLength > 0 ? field.maxLength : 65536) + '" placeholder="' + esc(field.placeholder || "") + '"' + readOnly) + description;
}

function globalManagementPluginMarkup(contributions) {
  if (!Array.isArray(contributions) || !contributions.length) return "";
  const cards = contributions.map(contribution => {
    const displayName = contribution.pluginDisplayName || contribution.pluginName || "";
    const title = contribution.title && String(contribution.title).trim() !== String(displayName).trim()
      ? '<strong>' + esc(contribution.title) + '</strong>'
      : "";
    return '<article class="global-management-plugin card" data-plugin-name="' + esc(contribution.pluginName) + '" data-plugin-contribution-id="' + esc(contribution.id) + '"><div class="section-heading"><div><h4>' + esc(displayName) + '</h4>' + title + (contribution.description ? '<p class="muted">' + esc(contribution.description) + '</p>' : "") + '</div></div><div class="plugin-contribution-fields">' + (contribution.fields || []).map(field => pluginFieldMarkup(contribution, field)).join("") + '</div></article>';
  }).join("");
  return '<section class="global-management-plugins"><div class="section-heading"><div><h3>插件设置</h3><p class="muted">由当前已启用的插件提供的用户级设置。</p></div></div>' + cards + '</section>';
}

function renderGlobalManagementModal() {
  if (!globalManagementDraft) return;
  const draft = globalManagementDraft;
  const body = globalManagementHostMarkup(draft.settings) + globalManagementPluginMarkup(draft.contributions);
  const footer = '<button class="primary" type="button" data-action="save-global-management">保存</button><button class="ghost" type="button" data-action="close-modal">取消</button>';
  showModal(modalShell("全局管理", body, footer), true, true);
  hydrateIcons(document);
}

export async function openGlobalManagement(userId) {
  if (!userById(userId)) return;
  try {
    const [settings, contributions] = await Promise.all([
      api("GET", "/api/users/" + encodeURIComponent(userId) + "/global-settings"),
      api("GET", "/api/plugin-contributions/user-global/" + encodeURIComponent(userId)),
    ]);
    globalManagementDraft = { userId, settings: settings || {}, contributions: contributions || [] };
    renderGlobalManagementModal();
  } catch (error) {
    toast(error.message, "error");
  }
}

function readGlobalManagementSettings() {
  const pressed = id => document.getElementById(id)?.getAttribute("aria-pressed") === "true";
  const value = id => document.getElementById(id)?.value || "";
  const runDays = parseInt(value("gm-general-run-days"), 10);
  const preValue = value("gm-advanced-pre").trim();
  const postValue = value("gm-advanced-post").trim();
  return {
    general: { syncEnabled: pressed("gm-general-sync"), enabled: pressed("gm-general-enabled"), runDays: Number.isNaN(runDays) ? -1 : runDays },
    notification: { syncEnabled: pressed("gm-notification-sync"), notifyEnabled: pressed("gm-notification-enabled"), smtpTo: value("gm-notification-smtp").trim() },
    advanced: {
      syncEnabled: pressed("gm-advanced-sync"),
      preRunScript: preValue.replace(/^%FIRST%\s*/, ""),
      preRunOnceOnly: preValue.startsWith("%FIRST%"),
      postRunScript: postValue.replace(/^%LAST%\s*/, ""),
      postRunOnFinalOnly: postValue.startsWith("%LAST%"),
    },
  };
}

function readPluginContributionValues(contribution) {
  const values = {};
  const container = document.querySelector('[data-plugin-name="' + CSS.escape(contribution.pluginName || "") + '"][data-plugin-contribution-id="' + CSS.escape(contribution.id || "") + '"]');
  for (const field of contribution.fields || []) {
    const type = String(field.type || "text").toLowerCase();
    const element = container?.querySelector('[data-plugin-field="' + CSS.escape(field.key) + '"]');
    if (!element || field.readOnly || type === "status") continue;
    if (type === "switch") {
      values[field.key] = element.getAttribute("aria-pressed") === "true";
    } else if (type === "multi-select") {
      values[field.key] = selectedPluginMultiSelectValues(element);
    } else if (type === "secret") {
      const action = element.dataset.secretAction === "clear"
        ? "clear"
        : element.value ? "set" : "keep";
      values[field.key] = action === "set" ? { action, value: element.value } : { action };
    } else {
      values[field.key] = element.value || "";
    }
  }
  return values;
}

export async function saveGlobalManagement() {
  if (!globalManagementDraft) return;
  const draft = globalManagementDraft;
  const settings = readGlobalManagementSettings();
  try {
    const saved = await api("PUT", "/api/users/" + encodeURIComponent(draft.userId) + "/global-settings", settings);
    for (const contribution of draft.contributions || []) {
      await api("PUT", "/api/plugin-contributions/user-global/" + encodeURIComponent(draft.userId) + "/" + encodeURIComponent(contribution.pluginName) + "/" + encodeURIComponent(contribution.id), {
        values: readPluginContributionValues(contribution),
      });
    }
    globalManagementDraft = null;
    closeModal();
    toast("全局设置已保存");
    await reloadUsers();
    return saved;
  } catch (error) {
    toast(error.message, "error");
  }
}

export function toggleGlobalManagementSwitch(target) {
  if (target.disabled) return;
  syncManagementSwitch(target, target.getAttribute("aria-pressed") !== "true");
}

export function toggleGlobalPluginSwitch(target) {
  if (target.disabled) return;
  syncManagementSwitch(target, target.getAttribute("aria-pressed") !== "true");
}

export function clearPluginSecret(target) {
  const field = target.closest(".global-management-plugin")?.querySelector('[data-plugin-field="' + CSS.escape(target.dataset.pluginField || "") + '"]');
  if (!field) return;
  field.value = "";
  field.dataset.secretAction = "clear";
  target.disabled = true;
}

function closePluginMultiSelects(except = null) {
  document.querySelectorAll('.plugin-multi-select-trigger[aria-expanded="true"]').forEach(trigger => {
    const wrapper = trigger.closest(".plugin-multi-select");
    if (wrapper === except) return;
    trigger.setAttribute("aria-expanded", "false");
    const menu = wrapper?.querySelector(".plugin-multi-select-menu");
    if (menu) menu.hidden = true;
  });
}

export function togglePluginMultiSelect(target) {
  const wrapper = target.closest(".plugin-multi-select");
  if (!wrapper || target.disabled) return;
  const expanded = target.getAttribute("aria-expanded") === "true";
  closePluginMultiSelects(wrapper);
  target.setAttribute("aria-expanded", expanded ? "false" : "true");
  const menu = wrapper.querySelector(".plugin-multi-select-menu");
  if (menu) menu.hidden = expanded;
}

export function syncPluginMultiSelectOption(target) {
  syncPluginMultiSelect(target.closest(".plugin-multi-select"));
}

if (typeof document !== "undefined") {
  document.addEventListener("click", event => {
    if (!event.target?.closest?.(".plugin-multi-select")) closePluginMultiSelects();
  });
  document.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    const openTrigger = document.querySelector('.plugin-multi-select-trigger[aria-expanded="true"]');
    if (!openTrigger) return;
    closePluginMultiSelects();
    event.preventDefault();
    event.stopImmediatePropagation();
  }, true);
  document.addEventListener("input", event => {
    const input = event.target?.closest?.('.plugin-secret-field input[data-plugin-type="secret"]');
    if (!input) return;
    input.dataset.secretAction = input.value ? "set" : "keep";
  });
}

function syncManagementSwitch(target, pressed) {
  if (!target) return;
  target.setAttribute("aria-pressed", pressed ? "true" : "false");
  target.dataset.state = pressed ? "on" : "off";
  const stateText = target.querySelector("[data-switch-state]");
  if (stateText) stateText.textContent = pressed ? "已启用" : "已停用";
}

function toggleManagementSwitch(target) {
  if (target.disabled) return;
  const on = target.getAttribute("aria-pressed") === "true";
  syncManagementSwitch(target, !on);
}

function syncManagementRunDays(target) {
  return target;
}

export const actions = {
  "open-global-user-modal": () => openGlobalUserModal(),
  "open-user-management": target => openUserManagement(target.dataset.userId),
  "open-global-management": target => withBusy(target, () => openGlobalManagement(target.dataset.userId)),
  "save-global-user": target => withBusy(target, () => saveGlobalUser()),
  "save-user-management": target => withBusy(target, () => saveUserManagement()),
  "save-global-management": target => withBusy(target, () => saveGlobalManagement()),
  "delete-global-user": target => deleteGlobalUser(target.dataset.userId),
  "confirm-delete-global-user": target => withBusy(target, () => confirmDeleteGlobalUser()),
  "user-management-back": () => userManagementBack(),
  "toggle-um-binding-edit": () => toggleUmBindingEdit(),
  "toggle-um-add-panel": () => toggleUmAddPanel(),
  "close-um-add-panel": () => closeUmAddPanel(),
  "toggle-um-add-item": target => toggleUmAddItem(target),
  "confirm-um-add-bindings": target => withBusy(target, () => confirmUmAddBindings()),
  "toggle-um-binding": target => toggleUmBinding(target),
  "collapse-um-binding": () => collapseUmBinding(),
  "set-um-subview": target => setUmSubview(target),
  "delete-user-binding": target => deleteUserBinding(target.dataset.userId, target.dataset.scriptId),
  "confirm-delete-user-binding": target => withBusy(target, () => confirmDeleteUserBinding(target.dataset.userId, target.dataset.scriptId)),
  "upload-user-avatar": target => uploadUserAvatar(target.dataset.userId),
  "remove-user-avatar": target => removeUserAvatar(target.dataset.userId),
  "edit-user-config-global": target => editGlobalUserConfig(target.dataset.userId, target.dataset.scriptId),
  "global-edit-config-done": target => withBusy(target, () => globalEditConfigAction(target.dataset.userId, target.dataset.scriptId, "done")),
  "global-edit-config-cancel": target => withBusy(target, () => globalEditConfigAction(target.dataset.userId, target.dataset.scriptId, "cancel")),
  "toggle-user-management-switch": target => toggleManagementSwitch(target),
  "sync-user-management-run-days": target => syncManagementRunDays(target),
  "toggle-global-management-switch": target => toggleGlobalManagementSwitch(target),
  "toggle-global-plugin-switch": target => toggleGlobalPluginSwitch(target),
  "clear-plugin-secret": target => clearPluginSecret(target),
  "toggle-plugin-multi-select": target => togglePluginMultiSelect(target),
  "sync-plugin-multi-select-option": target => syncPluginMultiSelectOption(target),
};
