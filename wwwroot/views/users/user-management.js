import { api, hydrateIcons } from "../../core/api.js";
import { $ } from "../../core/dom.js";
import { esc, scriptFallbackIcon, scriptPluginStatus, scriptPluginUnavailableMessage } from "../../core/format.js";
import { pathField, switchControl, textareaField, valueField } from "../../core/forms.js";
import { icon } from "../../core/icons.js";
import { state } from "../../core/state.js";
import { closeModal, confirmModal, modalShell, showModal } from "../../core/modal.js";
import { hasEntityNameConflict } from "../../core/entity-name.js";
import { setFieldError, setFieldInvalid, setRequiredFieldError, clearFieldError, toast, withBusy } from "../../core/ui.js";
import { initDndList } from "../../core/dnd.js";
import { pluginSlotMarkup, renderPluginSlots } from "../../core/plugin-slots.js";
import { PRE_ONLY_MARKER, POST_FINAL_MARKER, encodePrePost, splitPrePost } from "../../core/prepost.js";
import {
  MAX_ENTITY_NAME_BYTES,
  MAX_USER_REMARK_BYTES,
  availableScripts,
  cloneUser,
  getManagementDraft,
  reloadUsers,
  scriptById,
  setManagementDraft,
  toggleManagementSwitch,
  unavailableScriptMessage,
  userById,
} from "./shared.js";

function bindingIdPart(id) {
  return String(id || "script").replace(/[^a-zA-Z0-9_-]/g, "-");
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
  const draft = getManagementDraft();
  const idPart = bindingIdPart(binding.scriptInstanceId);
  const name = umScriptName(binding);
  const unavailableMessage = unavailableScriptMessage(binding.scriptInstanceId);
  const unavailable = !!unavailableMessage;
  const effective = binding.effective || binding;
  const locks = binding.locks || {};
  const notifyEnabled = effective.notifyEnabled !== false;
  const runDays = typeof effective.runDays === "number" ? effective.runDays : -1;
  const maxSuccessfulRuns = typeof effective.maxSuccessfulRunsPerDay === "number" ? effective.maxSuccessfulRunsPerDay : -1;
  const enabled = effective.enabled !== false;
  const maxRunDays = state.limits?.maxRunDays ?? 365;
  const maxSuccessfulRunsLimit = state.limits?.maxSuccessfulRunsPerDay ?? 10;
  const preValue = encodePrePost(PRE_ONLY_MARKER, effective.preRunOnceOnly, effective.preRunScript || "");
  const postValue = encodePrePost(POST_FINAL_MARKER, effective.postRunOnFinalOnly, effective.postRunScript || "");
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
      '<button class="danger um-binding-remove" type="button" data-action="delete-user-binding" data-testid="um-remove-binding" data-user-id="' + esc(draft.userId) + '" data-script-id="' + esc(binding.scriptInstanceId) + '">移除绑定</button>' +
      '<span class="um-binding-bottom-arrow" aria-hidden="true">' + icon("chevronRight") + "</span>" +
    "</div>";
  const mainView =
    '<button class="um-edit-config' + (unavailable ? ' is-unavailable' : '') + '" type="button" data-action="edit-user-config-global" data-user-id="' + esc(draft.userId) + '" data-script-id="' + esc(binding.scriptInstanceId) + '"' + (unavailable ? ' title="' + esc(unavailableMessage) + '"' : '') + '>' +
      '<span class="um-edit-config-copy"><strong>编辑配置</strong><span class="muted">启动主程序打开该脚本实例的用户配置</span></span>' +
      '<span class="um-edit-config-arrow">' + icon("chevronRight") + "</span>" +
    "</button>";
  const generalView =
    '<section class="um-binding-option-section um-view um-view-general"><div class="section-heading"><div><h4>通用</h4><p class="muted">绑定启用状态、运行天数和每日成功次数。</p></div></div>' +
      switchControl("um-" + idPart + "-enabled", "是否启用", "运行天数为 0 时不会参与运行", enabled, "toggle-user-management-switch", 'data-binding-field="enabled"' + (locks.general ? " disabled" : "")) +
      valueField("um-" + idPart + "-run-days", "运行天数", runDays, "number", 'data-binding-field="runDays" min="-1" max="' + esc(maxRunDays) + '" step="1" placeholder="' + esc(runDaysPlaceholder) + '"' + (locks.general ? " disabled" : ""), "-1 表示永久运行；0 表示停止该脚本实例；正数表示剩余运行天数，每日递减。") +
      valueField("um-" + idPart + "-max-success", "最多成功运行次数", maxSuccessfulRuns, "number", 'data-binding-field="maxSuccessfulRunsPerDay" min="-1" max="' + esc(maxSuccessfulRunsLimit) + '" step="1" placeholder="-1 不限制；正数达到上限后跳过"' + (locks.general ? " disabled" : ""), "-1 表示不限制；正整数达到上限后跳过，0 不是有效值。") +
      '<p class="muted helper-copy">当天成功次数达到上限后，后续手动运行和自动运行将记录为已跳过；失败、取消和已跳过不计入成功次数。</p>' +
      overrideHelper("general") +
    "</section>";
  const notifyView =
    '<section class="um-binding-option-section um-view um-view-notify"><div class="section-heading"><div><h4>通知</h4><p class="muted">用户绑定允许时发送运行结果通知。</p></div></div>' +
      switchControl("um-" + idPart + "-notify", "开启通知推送", "按用户绑定设置发送运行状态通知", notifyEnabled, "toggle-user-management-switch", 'data-binding-field="notifyEnabled"' + (locks.notification ? " disabled" : "")) +
      valueField("um-" + idPart + "-smtp", "SMTP 收件人", effective.smtpTo || "", "text", 'data-binding-field="smtpTo" placeholder="留空继承全局收件人"' + (locks.notification ? " disabled" : ""), "仅 SMTP 使用；留空时继承全局收件人，Webhook 不受影响。") +
      overrideHelper("notification") +
    "</section>";
  const advancedView =
    '<section class="um-binding-option-section um-view um-view-advanced"><div class="section-heading"><div><h4>高级</h4><p class="muted">任务前后脚本设置。</p></div></div>' +
      pathField("um-" + idPart + "-pre", "任务前运行脚本路径", preValue, "file", 'data-binding-field="preRunScript" placeholder="%FIRST% 开头填写仅首次运行"' + (locks.advanced ? " disabled" : ""), "脚本文件|*.exe;*.bat;*.cmd;*.ps1;*.py;*.js|所有文件|*.*", "", "选择后仍可手动编辑；%FIRST% 开头填写仅首次运行。") +
      pathField("um-" + idPart + "-post", "任务后运行脚本路径", postValue, "file", 'data-binding-field="postRunScript" placeholder="%LAST% 开头填写仅最终运行"' + (locks.advanced ? " disabled" : ""), "脚本文件|*.exe;*.bat;*.cmd;*.ps1;*.py;*.js|所有文件|*.*", "", "选择后仍可手动编辑；%LAST% 开头填写仅最终运行。") +
      overrideHelper("advanced") +
    "</section>";
  return '<article class="um-binding-card' + (umState.bindingEditMode ? ' is-binding-editing' : '') + (unavailable ? ' is-unavailable' : '') + '" data-testid="um-binding-card" data-dnd-id="' + esc(binding.scriptInstanceId) + '" data-binding-id="' + esc(binding.scriptInstanceId) + '" data-binding-enabled="' + (enabled ? "true" : "false") + '"' + (unavailable ? ' data-plugin-unavailable="true"' : '') + '>' +
    head +
    '<div class="um-binding-body"><div class="um-binding-options">' + mainView + generalView + notifyView + advancedView + '</div>' + pluginSlotMarkup("users.binding.sections", "binding-" + draft.userId + "-" + binding.scriptInstanceId, "user-binding-plugin-slot", { mode: "binding", primaryId: binding.scriptInstanceId, secondaryId: draft.userId }) + "</div>" +
  "</article>";
}

/** 用户管理界面状态：展开卡片、添加脚本面板与多选集合。 */
const umState = {
  expandedId: null,
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
  const draft = getManagementDraft();
  if (!draft) return;
  const user = draft.user;
  const scripts = availableScripts(user);
  const addItems = scripts.length
    ? '<div class="um-add-grid" id="um-add-grid">' + scripts.map(umAddItemMarkup).join("") + "</div>"
    : '<div class="empty compact-empty"><strong>没有可添加的脚本实例</strong><span>所有脚本实例都已绑定。</span></div>';
  const addArea =
    '<div class="um-add-area"' + (umState.addOpen ? " data-open" : "") + ">" +
      '<button class="um-add-script" type="button" data-action="toggle-um-add-panel" data-testid="um-add-script">' + icon("plus") + "<span>添加脚本</span></button>" +
      '<div class="um-add-panel secondary-surface" data-testid="um-add-panel">' +
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
      valueField("um-name", "用户名 <span class='req'>*</span>", user.name, "text", 'placeholder="输入用户名"', "用户名不区分大小写。") +
      textareaField("um-remark", "备注", user.remark || "", 'rows="3"', "可选", "为用户添加备注信息。") +
      (user.avatarUrl ? '<div class="user-avatar-setting"><span class="muted">已设置自定义头像</span><button class="tertiary" type="button" data-action="remove-user-avatar" data-user-id="' + esc(user.id) + '">移除自定义头像</button></div>' : "") +
    "</section>" +
    '<section class="subsection user-binding-section">' +
      '<div class="section-heading um-binding-section-heading"><div><h3>已绑定脚本实例</h3><p class="muted">每个绑定独立保存运行、通知和高级选项设置。</p></div>' + bindingEditToggle + "</div>" +
      addArea +
      bindingList +
    "</section>";
  const footer = '<button class="primary" type="button" data-action="save-user-management">保存</button><button class="ghost user-management-back" type="button" data-action="user-management-back">取消</button>';
  showModal(modalShell("用户管理", body, footer), true, true, true);
  syncUmState();
  void renderPluginSlots(document);
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
  const list = document.getElementById("um-binding-list");
  if (list) {
    const dragEnabled = umBindingDragEnabled();
    Array.from(list.querySelectorAll(".um-binding-card")).forEach(card => {
      const expanded = card.dataset.bindingId === umState.expandedId;
      card.classList.toggle("is-expanded", expanded);
      card.classList.toggle("is-binding-editing", umState.bindingEditMode);
      const toggle = card.querySelector(".um-binding-toggle");
      if (toggle) {
        const unavailable = card.dataset.pluginUnavailable === "true";
        toggle.disabled = umState.bindingEditMode;
        toggle.setAttribute("aria-disabled", unavailable || umState.bindingEditMode ? "true" : "false");
        toggle.setAttribute("aria-expanded", expanded ? "true" : "false");
      }
      const arrow = card.querySelector(".um-binding-bottom-arrow");
      if (arrow) {
        arrow.innerHTML = icon(expanded ? "chevronDown" : "chevronRight");
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
  setManagementDraft({ userId, user: cloneUser(user) });
  umState.expandedId = null;
  umState.addOpen = false;
  umState.bindingEditMode = false;
  umState.addSelected.clear();
  renderUserManagementModal();
}

function readBindingPayloads() {
  return Array.from(document.querySelectorAll("#um-binding-list [data-binding-id]")).map(card => {
    const read = field => card.querySelector('[data-binding-field="' + field + '"]');
    const pressed = field => read(field)?.getAttribute("aria-pressed") === "true";
    const raw = getManagementDraft()?.user?.bindings?.find(item => item.scriptInstanceId === card.dataset.bindingId) || {};
    const locks = raw.locks || {};
    const rawRunDays = parseInt((read("runDays")?.value || "-1"), 10);
    const runDays = Number.isNaN(rawRunDays) ? -1 : rawRunDays;
    const rawMaxSuccessfulRuns = parseInt((read("maxSuccessfulRunsPerDay")?.value || "-1"), 10);
    const maxSuccessfulRuns = Number.isNaN(rawMaxSuccessfulRuns) ? -1 : rawMaxSuccessfulRuns;
    const pre = splitPrePost(PRE_ONLY_MARKER, read("preRunScript")?.value || "");
    const post = splitPrePost(POST_FINAL_MARKER, read("postRunScript")?.value || "");
    const payload = {
      scriptInstanceId: card.dataset.bindingId,
      enabled: locks.general ? raw.enabled !== false : pressed("enabled"),
      notifyEnabled: locks.notification ? raw.notifyEnabled !== false : pressed("notifyEnabled"),
      smtpTo: locks.notification ? (raw.smtpTo || "") : (read("smtpTo")?.value.trim() || ""),
      preRunScript: locks.advanced ? (raw.preRunScript || "") : pre.value,
      preRunOnceOnly: locks.advanced ? raw.preRunOnceOnly === true : pre.onceOnly,
      postRunScript: locks.advanced ? (raw.postRunScript || "") : post.value,
      postRunOnFinalOnly: locks.advanced ? raw.postRunOnFinalOnly === true : post.onceOnly,
      runDays: locks.general ? (typeof raw.runDays === "number" ? raw.runDays : -1) : runDays,
      maxSuccessfulRunsPerDay: locks.general
        ? (typeof raw.maxSuccessfulRunsPerDay === "number" ? raw.maxSuccessfulRunsPerDay : -1)
        : maxSuccessfulRuns,
    };
    return payload;
  });
}

export async function saveUserManagement() {
  const draft = getManagementDraft();
  if (!draft) return;
  const name = $("#um-name")?.value.trim() || "";
  if (!name) {
    setRequiredFieldError("um-name");
    toast("请填写用户名", "error");
    return;
  }
  if (new TextEncoder().encode(name).length > MAX_ENTITY_NAME_BYTES) {
    setFieldError("um-name", `用户名最多 ${MAX_ENTITY_NAME_BYTES} 字节`);
    toast(`用户名最多 ${MAX_ENTITY_NAME_BYTES} 字节`, "error");
    return;
  }
  if (hasEntityNameConflict(state.users, name, draft.userId)) {
    setFieldInvalid("um-name");
    toast("用户名已存在，请使用其他名称", "error");
    return;
  }
  clearFieldError("um-name");
  const remark = $("#um-remark")?.value.trim() || "";
  if (new TextEncoder().encode(remark).length > MAX_USER_REMARK_BYTES) {
    setFieldError("um-remark", `备注最多 ${MAX_USER_REMARK_BYTES} 字节`);
    toast(`备注最多 ${MAX_USER_REMARK_BYTES} 字节`, "error");
    return;
  }
  clearFieldError("um-remark");
  const userId = draft.userId;
  const bindings = readBindingPayloads();
  try {
    await api("PUT", "/api/users/" + encodeURIComponent(userId), { name, remark });
    for (const binding of bindings) {
      await api("PUT", "/api/users/" + encodeURIComponent(userId) + "/bindings/" + encodeURIComponent(binding.scriptInstanceId), binding);
    }
    setManagementDraft(null);
    closeModal();
    toast("用户设置已保存");
    await reloadUsers();
  } catch (error) {
    if (error?.code === "duplicate_name") {
      setFieldInvalid("um-name");
      toast("用户名已存在，请使用其他名称", "error");
      return;
    }
    toast(error.message, "error");
  }
}

async function refreshManagedUser(knownBindings = []) {
  const draft = getManagementDraft();
  if (!draft) return;
  try {
    state.users = await api("GET", "/api/users");
    const user = userById(draft.userId);
    if (!user) {
      setManagementDraft(null);
      closeModal();
      await reloadUsers();
      return;
    }
    for (const binding of knownBindings) mergeManagedBinding(user, binding);
    setManagementDraft({ userId: draft.userId, user: cloneUser(user) });
    umState.expandedId = null;
    umState.addOpen = false;
    umState.addSelected.clear();
    renderUserManagementModal();
  } catch (error) {
    // 新增绑定的 POST 已成功时，即使后续列表刷新失败也先把服务端返回的卡片呈现出来。
    const current = getManagementDraft();
    if (current && knownBindings.length) {
      for (const binding of knownBindings) mergeManagedBinding(current.user, binding);
      umState.expandedId = null;
      umState.addOpen = false;
      umState.addSelected.clear();
      renderUserManagementModal();
    }
    toast(error.message, "error");
  }
}

function restoreManagedBindingOrder() {
  const list = document.getElementById("um-binding-list");
  const bindings = getManagementDraft()?.user?.bindings;
  if (!list || !Array.isArray(bindings)) return;
  const cards = new Map(Array.from(list.querySelectorAll("[data-dnd-id]")).map(card => [card.dataset.dndId, card]));
  for (const binding of bindings) {
    const card = cards.get(binding.scriptInstanceId);
    if (card) list.appendChild(card);
  }
}

async function reorderManagedBindings(ids) {
  const draft = getManagementDraft();
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
    if (getManagementDraft() === draft) toast("已绑定脚本实例顺序已保存");
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
  const draft = getManagementDraft();
  if (!draft) return;
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
        maxSuccessfulRunsPerDay: -1,
      };
      addedBindings.push((await api("POST", "/api/users/" + encodeURIComponent(draft.userId) + "/bindings", payload)) || payload);
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
  } else {
    umState.expandedId = id;
    umState.addOpen = false;
  }
  syncUmState();
}

/** 收回展开的绑定卡片。 */
export function collapseUmBinding() {
  umState.expandedId = null;
  umState.addOpen = false;
  syncUmState();
}

/** 用户管理弹窗底部取消。 */
export function userManagementBack() {
  closeModal();
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

export async function uploadUserAvatar(id) {
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
        if (getManagementDraft()?.userId === id) await refreshManagedUser();
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
    if (getManagementDraft()?.userId === id) await refreshManagedUser();
    else await reloadUsers();
  } catch (error) {
    toast(error.message, "error");
  }
}

function syncManagementRunDays(target) {
  return target;
}

export const actions = {
  "open-user-management": target => openUserManagement(target.dataset.userId),
  "save-user-management": target => withBusy(target, () => saveUserManagement()),
  "user-management-back": () => userManagementBack(),
  "toggle-um-binding-edit": () => toggleUmBindingEdit(),
  "toggle-um-add-panel": () => toggleUmAddPanel(),
  "close-um-add-panel": () => closeUmAddPanel(),
  "toggle-um-add-item": target => toggleUmAddItem(target),
  "confirm-um-add-bindings": target => withBusy(target, () => confirmUmAddBindings()),
  "toggle-um-binding": target => toggleUmBinding(target),
  "collapse-um-binding": () => collapseUmBinding(),
  "delete-user-binding": target => deleteUserBinding(target.dataset.userId, target.dataset.scriptId),
  "confirm-delete-user-binding": target => withBusy(target, () => confirmDeleteUserBinding(target.dataset.userId, target.dataset.scriptId)),
  "upload-user-avatar": target => uploadUserAvatar(target.dataset.userId),
  "remove-user-avatar": target => removeUserAvatar(target.dataset.userId),
  "toggle-user-management-switch": target => toggleManagementSwitch(target),
  "sync-user-management-run-days": target => syncManagementRunDays(target),
};
