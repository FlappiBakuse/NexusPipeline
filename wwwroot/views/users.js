import { api } from "../core/api.js";
import { $ as $dom } from "../core/dom.js";
import { esc } from "../core/format.js";
import { pageHeader, valueField } from "../core/forms.js";
import { icon } from "../core/icons.js";
import { pagerMarkup, registerPager, replacePageOrder } from "../core/pager.js";
import { isCurrent, state } from "../core/state.js";
import { closeModal, confirmModal, modalShell, showModal } from "../core/modal.js";
import { navActive, render, setFieldError, clearFieldError, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { initDndList } from "../core/dnd.js";

let userModalScriptId = "";
let userEditingName = null;
let userDraft = null;
let userPage = 1;
const USER_PAGE_SIZE = 10;

export async function pageScriptUsers(scriptId, token) {
  const page = "scripts/" + scriptId + "/users";
  if (!isCurrent(page, token)) return;
  navActive("scripts");
  setTopbarTitle("用户管理");
  let scripts;
  try { scripts = await api("GET", "/api/scripts"); }
  catch (error) { render(`<div class="empty"><strong>加载用户失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent(page, token)) return;
  state.scripts = scripts;
  const script = scripts.find(item => item.id === scriptId);
  if (!script) { render('<div class="empty"><strong>脚本实例不存在</strong><a class="back-link" href="#/scripts">返回脚本实例</a></div>'); return; }
  const users = script.users || [];
  const atLimit = !!(state.limits && users.length >= state.limits.maxUsersPerScript);
  const action = `<button class="primary" type="button" data-action="open-user-modal" data-id="${script.id}" ${atLimit ? "disabled" : ""}>添加用户${atLimit ? `（${users.length}/${state.limits.maxUsersPerScript}）` : ""}</button>`;
  const totalPages = Math.max(1, Math.ceil(users.length / USER_PAGE_SIZE));
  if (userPage > totalPages) userPage = totalPages;
  const pageItems = users.slice((userPage - 1) * USER_PAGE_SIZE, userPage * USER_PAGE_SIZE);
  const usersMarkup = users.length ? `<section class="card list-surface dnd-list">${pageItems.map((user, pageIndex) => {
    const userIndex = (userPage - 1) * USER_PAGE_SIZE + pageIndex;
    return `<article class="user-card" data-dnd-id="${esc(user.name)}">
    <span class="drag-handle" role="button" tabindex="0" aria-label="拖拽排序（方向键调整顺序）" title="拖拽排序">${icon("grip")}</span>
    <div class="list-item-head"><div><div class="list-item-title"><strong>${esc(user.name)}</strong>${user.enabled ? '<span class="badge ok">已启用</span>' : '<span class="badge muted">已禁用</span>'}</div>
      <div class="qk-row">任务前脚本：${user.preRunScript ? `<span class="mono">${esc(user.preRunScript)}</span>${user.preRunOnceOnly ? "（仅首次）" : ""}` : '<span class="muted">未设置</span>'}</div>
      <div class="qk-row">任务后脚本：${user.postRunScript ? `<span class="mono">${esc(user.postRunScript)}</span>${user.postRunOnFinalOnly ? "（仅最终完成）" : ""}` : '<span class="muted">未设置</span>'}</div>
    </div>
      <div class="action-row"><div class="user-actions">
        <button class="sm ghost" type="button" data-action="edit-user-config" data-id="${script.id}" data-name="${esc(user.name)}">编辑配置</button>
        <button class="sm ghost" type="button" data-action="edit-user" data-id="${script.id}" data-name="${esc(user.name)}">编辑用户</button>
        <button class="sm danger" type="button" data-action="delete-user" data-id="${script.id}" data-name="${esc(user.name)}">删除用户</button>
      </div></div>
    </div>
  </article>`;
  }).join("")}${users.length > USER_PAGE_SIZE ? pagerMarkup("users", userPage, USER_PAGE_SIZE, users.length) : ""}</section>` : '<div class="empty"><strong>暂无用户</strong>点击右上角「添加用户」创建。</div>';
  render(pageHeader("用户配置", `${esc(script.name)} · 用户管理`, "为不同用户保存独立配置，运行时会自动交换并还原。", action) + `<div class="back-row"><a class="back-link" href="#/scripts">${icon("arrowLeft")} 返回脚本实例</a></div>${usersMarkup}`);
  registerPager("users", p => { userPage = p; pageScriptUsers(scriptId, state.routeToken); });
  restoreEditSessionCard(scriptId);
  wireUserDnd(scriptId);
}

/** 拖拽排序：只改变当前分页区间，其他分页保持原位置与相对顺序。 */
function wireUserDnd(scriptId) {
  const list = $dom(".dnd-list");
  if (!list) return;
  initDndList(list, { onDrop: (names) => reorderUsers(scriptId, names) });
}

/** 把当前页新顺序写回全量用户列表，提交 PUT users/order（names 协议）。 */
async function reorderUsers(scriptId, visibleNames) {
  const script = state.scripts.find(item => item.id === scriptId);
  if (!script) return;
  const users = script.users || [];
  const full = replacePageOrder(users, userPage, USER_PAGE_SIZE, visibleNames, user => user.name);
  try {
    await api("PUT", `/api/scripts/${scriptId}/users/order`, { names: full.map(user => user.name) });
    toast("用户顺序已保存");
    await pageScriptUsers(scriptId, state.routeToken);
  } catch (error) {
    toast(error.message, "error");
    await pageScriptUsers(scriptId, state.routeToken);
  }
}

/** 刷新后恢复进行中的「配置编辑中」锁定卡片（后端会话仍在，用户可继续完成/取消）。 */
async function restoreEditSessionCard(scriptId) {
  if (!$dom(".modal-mask")) {
    try {
      const sessions = await api("GET", "/api/scripts/edit-sessions");
      const session = (sessions || []).find(item => item.scriptId === scriptId);
      if (session) showEditConfigCard(scriptId, session.userName);
    } catch (error) { /* 查询失败静默，下次进入页面重试 */ }
  }
}

export function openUserModal(scriptId, userName = "") {
  const script = state.scripts.find(item => item.id === scriptId);
  if (!script) { toast("脚本不存在", "error"); return; }
  const user = userName ? (script.users || []).find(item => item.name === userName) : null;
  userModalScriptId = scriptId;
  userEditingName = userName || null;
  userDraft = {
    name: user?.name || "", enabled: user?.enabled !== false, preRunScript: user?.preRunScript || "", preRunOnceOnly: !!user?.preRunOnceOnly,
    postRunScript: user?.postRunScript || "", postRunOnFinalOnly: !!user?.postRunOnFinalOnly,
  };
  const d = userDraft;
  const body = `${valueField("um-name", "用户名 <span class='req'>*</span>", d.name, "text", 'placeholder="脚本内不可重复"')}
    <div class="toggle-row settings-option settings-option-card"><button class="mode-toggle" type="button" data-action="toggle-um-flag" data-flag="um-enabled" id="um-enabled" aria-pressed="${d.enabled ? "true" : "false"}">启用用户</button><span class="muted">禁用后不可选用于运行</span></div>
    <div class="subsection"><h3>任务前运行脚本</h3>${valueField("um-pre", "脚本路径（填写则启用，留空不启用）", d.preRunScript)}<div class="toggle-row settings-option settings-option-card"><button class="mode-toggle" type="button" data-action="toggle-um-flag" data-flag="um-pre-once" id="um-pre-once" aria-pressed="${d.preRunOnceOnly ? "true" : "false"}">仅首次执行</button><span class="muted">重试时不再执行</span></div></div>
    <div class="subsection"><h3>任务后运行脚本</h3>${valueField("um-post", "脚本路径（填写则启用，留空不启用）", d.postRunScript)}<div class="toggle-row settings-option settings-option-card"><button class="mode-toggle" type="button" data-action="toggle-um-flag" data-flag="um-post-final" id="um-post-final" aria-pressed="${d.postRunOnFinalOnly ? "true" : "false"}">仅最终完成</button><span class="muted">仅最终运行完成启用</span></div></div>`;
    showModal(modalShell(user ? "编辑用户" : "添加用户", body, '<button class="primary" type="button" data-action="save-user">保存</button><button class="ghost" type="button" data-action="close-modal">取消</button>'));
}

export async function saveUser() {
  const name = $dom("#um-name")?.value.trim();
  if (!name) { setFieldError("um-name", "请填写用户名"); toast("请填写用户名", "error"); return; }
  clearFieldError("um-name");
  const payload = { name, enabled: $dom("#um-enabled")?.getAttribute("aria-pressed") === "true", preRunScript: $dom("#um-pre").value.trim(), preRunOnceOnly: $dom("#um-pre-once")?.getAttribute("aria-pressed") === "true", postRunScript: $dom("#um-post").value.trim(), postRunOnFinalOnly: $dom("#um-post-final")?.getAttribute("aria-pressed") === "true" };
  try {
    const base = "/api/scripts/" + userModalScriptId + "/users";
    if (userEditingName) await api("PUT", base + "/" + encodeURIComponent(userEditingName), payload);
    else await api("POST", base, payload);
    const id = userModalScriptId;
    closeModal(); toast("用户已保存");
    await pageScriptUsers(id, state.routeToken);
  } catch (error) { toast(error.message, "error"); }
}

export function deleteUser(scriptId, userName) {
  confirmModal("删除用户", `确定删除用户「${esc(userName)}」？（该用户保存的配置会一并删除）`, "confirm-delete-user", { id: scriptId, name: userName });
}

export async function confirmDeleteUser(scriptId, userName) {
  try { await api("DELETE", `/api/scripts/${scriptId}/users/${encodeURIComponent(userName)}`); closeModal(); toast("用户已删除"); await pageScriptUsers(scriptId, state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export async function editUserConfig(scriptId, userName) {
  try {
    await api("POST", `/api/scripts/${scriptId}/users/${encodeURIComponent(userName)}/edit-config`, { action: "start" });
    showEditConfigCard(scriptId, userName);
  } catch (error) { toast(error.message, "error"); }
}

/** 「配置编辑中」锁定卡片（Esc/遮罩/× 均不可关闭，只能完成或取消）；刷新后由用户管理页自动恢复。 */
function showEditConfigCard(scriptId, userName) {
  showModal(modalShell("配置编辑中", `<p class="modal-copy">主程序已启动（不带参数）。请自行设置好当前用户「${esc(userName)}」的脚本配置。设置完成后点击「完成」保存，或点击「取消」放弃本次修改。</p>`, `<button class="primary" type="button" data-action="edit-config-done" data-id="${scriptId}" data-name="${esc(userName)}">完成</button><button class="ghost" type="button" data-action="edit-config-cancel" data-id="${scriptId}" data-name="${esc(userName)}">取消</button>`), false, true);
}

export async function editConfigAction(scriptId, userName, action) {
  try { await api("POST", `/api/scripts/${scriptId}/users/${encodeURIComponent(userName)}/edit-config`, { action }); closeModal(); toast(action === "done" ? "已保存当前用户配置" : "已取消，配置已还原"); await pageScriptUsers(scriptId, state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "manage-users": target => { location.hash = "#/scripts/" + target.dataset.id + "/users"; },
  "open-user-modal": target => openUserModal(target.dataset.id),
  "edit-user": target => openUserModal(target.dataset.id, target.dataset.name),
  "save-user": target => withBusy(target, () => saveUser()),
  "delete-user": target => deleteUser(target.dataset.id, target.dataset.name),
  "confirm-delete-user": target => withBusy(target, () => confirmDeleteUser(target.dataset.id, target.dataset.name)),
  "edit-user-config": target => editUserConfig(target.dataset.id, target.dataset.name),
  "edit-config-done": target => withBusy(target, () => editConfigAction(target.dataset.id, target.dataset.name, "done")),
  "edit-config-cancel": target => withBusy(target, () => editConfigAction(target.dataset.id, target.dataset.name, "cancel")),
  "toggle-um-flag": target => { const btn = $dom("#" + target.dataset.flag); if (btn) btn.setAttribute("aria-pressed", btn.getAttribute("aria-pressed") === "true" ? "false" : "true"); },
};
